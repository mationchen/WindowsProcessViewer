namespace WindowsProcessViewer.Services;

/// <summary>关闭建议：可以关闭 / 谨慎关闭 / 建议保留 / 禁止关闭。</summary>
public enum CloseAdvice
{
    Safe,
    Caution,
    Keep,
    Forbidden,
}

public sealed record ProcessKnowledge(
    string Software,
    string Purpose,
    CloseAdvice Advice,
    string CloseMethod,
    string Category,
    string Risk,
    string Notes = "");

/// <summary>
/// 内置进程知识库：覆盖 Windows 自带进程与常见国内/国外软件。
/// 命中不了的进程由 <see cref="ProcessAnalyzer"/> 用文件信息 + 路径规则推断。
/// </summary>
public static class KnowledgeBase
{
    private const CloseAdvice S = CloseAdvice.Safe;
    private const CloseAdvice C = CloseAdvice.Caution;
    private const CloseAdvice K = CloseAdvice.Keep;
    private const CloseAdvice F = CloseAdvice.Forbidden;

    private const string Kill = "任务管理器 → 选中进程 → 结束任务；也可在本程序右键“结束进程”";
    private const string Exit = "在该软件界面选择“退出/关闭”；或任务管理器结束任务";
    private const string Svc = "不要直接结束进程：请用 services.msc 停止对应服务";
    private const string Never = "无法关闭：系统核心进程，只能关机或重启时终止";
    private const string ExplorerRestart = "结束任务后桌面会消失，需在任务管理器“运行新任务”执行 explorer.exe 恢复";

    /// <summary>绝对不能结束的进程（结束会导致蓝屏/立即重启/注销）。</summary>
    public static readonly IReadOnlySet<string> ForbiddenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "idle", "registry", "memory compression",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
    };

    private static readonly Dictionary<string, ProcessKnowledge> Map = new(StringComparer.OrdinalIgnoreCase);

    static KnowledgeBase()
    {
        SeedCore();
        SeedWindows();
        SeedSecurity();
        SeedThirdParty();
        SeedDevTools();
        SeedHardwareAndTools();
    }

    public static ProcessKnowledge? Find(string processName)
    {
        var key = Normalize(processName);
        return Map.TryGetValue(key, out var knowledge) ? knowledge : null;
    }

    public static bool IsForbidden(string processName) => ForbiddenNames.Contains(Normalize(processName));

    private static string Normalize(string name)
    {
        var text = (name ?? "").Trim();
        if (text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            text = text[..^4];
        return text.ToLowerInvariant();
    }

    private static void Add(string[] names, string software, string purpose, CloseAdvice advice,
        string category, string risk, string closeMethod = "", string notes = "")
    {
        var knowledge = new ProcessKnowledge(software, purpose, advice, closeMethod, category, risk, notes);
        foreach (var name in names)
            Map[Normalize(name)] = knowledge;
    }

    // ---------------------------------------------------------------- 内核与核心
    private static void SeedCore()
    {
        Add(["system"], "Microsoft Windows", "内核态系统进程（PID 4），承载内核与驱动线程，不是普通程序",
            F, "系统核心", "极高", Never);
        Add(["system idle process", "idle"], "Microsoft Windows", "CPU 空闲时间统计，不是真实程序，占用 0 属正常",
            F, "系统核心", "极高", Never);
        Add(["registry"], "Microsoft Windows", "内核注册表配置单元，保存内存中的注册表数据",
            F, "系统核心", "极高", Never);
        Add(["memory compression"], "Microsoft Windows", "内存压缩，把不常用页面压缩后留在内存中，减少磁盘交换",
            F, "系统核心", "极高", Never);
        Add(["smss"], "Microsoft Windows", "会话管理器，创建用户会话并启动 winlogon/csrss",
            F, "系统核心", "极高", Never);
        Add(["csrss"], "Microsoft Windows", "客户端/服务器运行时子系统，负责控制台与进程/线程管理",
            F, "系统核心", "极高", Never, "结束会立即蓝屏");
        Add(["wininit"], "Microsoft Windows", "Windows 初始化进程，启动 services/lsass/lsaiso",
            F, "系统核心", "极高", Never);
        Add(["winlogon"], "Microsoft Windows", "登录管理器，负责登录、锁屏与用户会话",
            F, "系统核心", "极高", Never);
        Add(["services"], "Microsoft Windows", "服务控制管理器（SCM），所有 Windows 服务的父进程",
            F, "系统核心", "极高", Never);
        Add(["lsass"], "Microsoft Windows", "本地安全认证子系统，处理登录凭据与安全策略",
            F, "系统核心", "极高", Never, "结束会触发重启，且可能丢失未保存工作");
        Add(["lsaiso"], "Microsoft Windows", "LSA 隔离进程，为 lsass 提供虚拟化保护",
            F, "系统核心", "极高", Never);
        Add(["svchost"], "Microsoft Windows", "服务宿主进程，一个进程常承载多个系统服务（如网络、音频、更新）",
            C, "Windows 服务", "中", Svc, "任务管理器里展开可看到具体服务名");
        Add(["fontdrvhost"], "Microsoft Windows", "字体驱动宿主，为应用提供字体渲染",
            C, "Windows 子系统", "中", Kill);
        Add(["dwm"], "Microsoft Windows", "桌面窗口管理器，负责窗口合成、透明与动画效果",
            C, "Windows 子系统", "中", "结束会导致屏幕短暂黑屏，Windows 会自动重启该进程");
        Add(["sihost"], "Microsoft Windows", "Shell 基础设施宿主，开始菜单/任务栏依赖它",
            C, "Windows 子系统", "中", "结束后任务栏可能闪烁并自动恢复");
        Add(["explorer"], "Microsoft Windows", "资源管理器：桌面、任务栏、文件窗口都由它提供",
            C, "Windows 子系统", "中", ExplorerRestart, "关闭不会关机，只是桌面重启");
        Add(["ctfmon"], "Microsoft Windows", "输入法与文本服务框架宿主，中文输入依赖它",
            C, "Windows 子系统", "中", "结束后输入法可能异常，注销或重启可恢复");
        Add(["taskhostw"], "Microsoft Windows", "通用任务宿主，运行计划任务与部分系统后台任务",
            C, "Windows 子系统", "中", Kill);
        Add(["runtimebroker"], "Microsoft Windows", "UWP 应用权限代理，商店应用访问文件/设备时使用",
            S, "Windows 组件", "低", Kill);
        Add(["shellexperiencehost"], "Microsoft Windows", "开始菜单与操作中心界面宿主",
            S, "Windows 组件", "低", Kill);
        Add(["startmenuexperiencehost"], "Microsoft Windows", "Windows 10/11 开始菜单界面",
            S, "Windows 组件", "低", Kill);
        Add(["searchhost"], "Microsoft Windows", "Windows 11 搜索界面",
            S, "Windows 组件", "低", Kill);
        Add(["searchapp"], "Microsoft Windows", "Windows 10 搜索界面",
            S, "Windows 组件", "低", Kill);
        Add(["searchindexer"], "Microsoft Windows", "Windows 搜索索引服务（WSearch），建立文件索引",
            C, "Windows 服务", "中", "services.msc → Windows Search → 停止", "索引时占用磁盘较高属正常");
        Add(["searchprotocolhost", "searchfilterhost"], "Microsoft Windows", "搜索索引的辅助进程，读取文件内容时临时启动",
            S, "Windows 组件", "低", Kill);
        Add(["searchui"], "Microsoft Windows", "搜索界面辅助进程",
            S, "Windows 组件", "低", Kill);
        Add(["audiodg"], "Microsoft Windows", "音频设备图形隔离，负责音频效果处理",
            C, "Windows 子系统", "中", "结束后当前声音会中断，系统会自动重启该进程");
        Add(["conhost"], "Microsoft Windows", "控制台窗口宿主，命令行程序的窗口由它提供",
            C, "Windows 组件", "低", "关闭对应命令行窗口即可退出");
        Add(["dllhost"], "Microsoft Windows", "COM+ 代理进程，运行组件服务中的 COM 对象",
            C, "Windows 组件", "中", Kill);
        Add(["spoolsv"], "Microsoft Windows", "打印后台处理服务，管理所有打印任务",
            K, "Windows 服务", "高", "services.msc → Print Spooler → 停止", "停止后无法打印");
        Add(["wudfhost"], "Microsoft Windows", "用户模式驱动框架宿主，部分设备驱动运行在此",
            C, "Windows 组件", "中", Kill);
        Add(["dashost"], "Microsoft Windows", "设备关联服务宿主（投屏、蓝牙等设备关联）",
            C, "Windows 组件", "中", Kill);
        Add(["sppsvc"], "Microsoft Windows", "软件保护服务，负责 Windows/Office 激活",
            K, "Windows 服务", "高", Svc, "停止后可能提示未激活");
        Add(["werfault", "werfaultsecure"], "Microsoft Windows", "Windows 错误报告，程序崩溃时收集信息",
            S, "Windows 组件", "低", Kill);
        Add(["backgroundtaskhost"], "Microsoft Windows", "UWP 应用后台任务宿主",
            S, "Windows 组件", "低", Kill);
        Add(["applicationframehost"], "Microsoft Windows", "UWP 应用窗口框架宿主",
            C, "Windows 组件", "低", Kill);
        Add(["textinputhost"], "Microsoft Windows", "触摸键盘与手写输入界面",
            S, "Windows 组件", "低", Kill);
        Add(["lockapp"], "Microsoft Windows", "锁屏界面进程，锁屏时显示时间与背景",
            C, "Windows 组件", "中", Kill);
        Add(["logonui"], "Microsoft Windows", "登录界面（输入密码的界面）",
            C, "Windows 组件", "中", Kill);
        Add(["userinit"], "Microsoft Windows", "登录后初始化用户环境并启动 explorer",
            K, "系统核心", "高", Never);
        Add(["wmiprvse"], "Microsoft Windows", "WMI 提供程序宿主，管理系统查询（很多工具/监控依赖它）",
            C, "Windows 服务", "中", "一般会自行重启；持续高 CPU 可重启 Winmgmt 服务");
        Add(["wmiprvse.exe"], "Microsoft Windows", "WMI 提供程序宿主，管理系统查询",
            C, "Windows 服务", "中", "一般会自行重启");
        Add(["taskmgr"], "Microsoft Windows", "任务管理器",
            S, "系统工具", "低", Kill);
        Add(["regedit"], "Microsoft Windows", "注册表编辑器",
            C, "系统工具", "中", "改动注册表前请先备份", "误改注册表可能导致系统异常");
        Add(["mmc"], "Microsoft Windows", "管理控制台宿主（服务、设备管理器等管理单元）",
            C, "系统工具", "中", Kill);
        Add(["control"], "Microsoft Windows", "控制面板",
            S, "系统工具", "低", Kill);
        Add(["consent"], "Microsoft Windows", "UAC 用户账户控制确认框",
            C, "Windows 组件", "中", "等待用户在弹窗中做出选择，不要结束");
        Add(["snippingtool", "screenclippinghost"], "Microsoft Windows", "截图工具",
            S, "系统工具", "低", Kill);
        Add(["systemsettings"], "Microsoft Windows", "Windows 设置应用",
            S, "系统工具", "低", Kill);
        Add(["msedgewebview2"], "Microsoft Edge WebView2", "内嵌浏览器组件，很多软件（微信、Office 加载项等）用它显示网页内容",
            C, "运行库", "中", "不建议直接结束：会让宿主软件界面异常；应关闭对应软件");
        Add(["mstsc"], "Microsoft Windows", "远程桌面连接客户端",
            S, "系统工具", "低", Kill);
        Add(["rdpclip"], "Microsoft Windows", "远程桌面剪贴板重定向",
            S, "系统工具", "低", Kill);
        Add(["gamebar", "gamebarftserver", "xboxgamebarwidgets"], "Microsoft Windows", "Xbox Game Bar 游戏工具栏",
            S, "系统工具", "低", Kill);
        Add(["widgets", "widgetservice"], "Microsoft Windows", "Windows 小组件（资讯/天气）面板",
            S, "Windows 组件", "低", Kill);
        Add(["phoneexperiencehost", "yourphone"], "Microsoft Windows", "手机连接（Your Phone）",
            S, "第三方软件", "低", Exit);
        Add(["cmd"], "Microsoft Windows", "命令提示符",
            C, "系统工具", "中", "关闭对应命令行窗口");
        Add(["powershell", "pwsh"], "Microsoft PowerShell", "命令行脚本环境（系统管理常用）",
            C, "开发工具", "中", "关闭对应窗口；若为脚本宿主，先确认来源", "常被恶意脚本利用，来源不明时警惕");
        Add(["rundll32"], "Microsoft Windows", "运行 DLL 导出的通用宿主程序",
            C, "系统组件", "中", Kill, "正常系统也会用到；若命令行指向临时目录需警惕");
        Add(["mshta"], "Microsoft Windows", "HTML 应用宿主（.hta 脚本）",
            C, "系统组件", "高", Kill, "常被恶意脚本滥用，来源不明建议结束");
        Add(["wscript", "cscript"], "Microsoft Windows", "Windows 脚本宿主（VBS/JS 脚本）",
            C, "系统组件", "高", Kill, "常被恶意脚本滥用，来源不明建议结束");
        Add(["msiexec"], "Microsoft Windows", "Windows Installer，安装/卸载软件时运行",
            C, "安装程序", "中", "安装过程中不要结束，否则软件可能装坏");
    }

    // ---------------------------------------------------------------- Windows 更新/维护
    private static void SeedWindows()
    {
        Add(["tiworker"], "Microsoft Windows", "Windows 模块安装程序工作进程，安装更新时运行",
            C, "Windows 更新", "中", "更新安装期间不要结束；空闲时会自行退出");
        Add(["trustedinstaller"], "Microsoft Windows", "Windows 模块安装程序服务，拥有系统文件修改权限",
            K, "Windows 更新", "高", Svc);
        Add(["wuauclt", "usoclient", "mousocoreworker", "waasmedicagent", "usosvc"], "Microsoft Windows", "Windows 更新客户端/协调进程",
            C, "Windows 更新", "中", "正在更新时不要结束；可通过“设置 → Windows 更新”暂停更新");
        Add(["vssvc"], "Microsoft Windows", "卷影复制服务，系统还原与备份使用",
            C, "Windows 服务", "中", Svc);
        Add(["sgrmbroker"], "Microsoft Windows", "系统保护运行时监控（Device Guard 相关）",
            K, "Windows 服务", "高", Svc);
        Add(["securityhealthservice", "securityhealthsystray"], "Microsoft Windows", "Windows 安全中心（ defender 状态托盘）",
            K, "安全防护", "高", "不建议关闭；结束托盘图标会自动恢复");
        Add(["smartscreen"], "Microsoft Windows", "SmartScreen 应用与网页筛选",
            C, "安全防护", "中", Kill);
        Add(["msmpeng", "nissrv", "mpdefendercoreservice"], "Microsoft Defender", "Windows 自带杀毒引擎与网络检查",
            K, "安全防护", "高", "受篡改保护，通常无法结束；请在安全中心里管理",
            "强行结束会导致实时防护中断");
    }

    // ---------------------------------------------------------------- 安全软件
    private static void SeedSecurity()
    {
        Add(["hipstray", "hipsdaemon", "usysdiag"], "火绒安全", "火绒安全软件的托盘与防护驱动",
            K, "安全防护", "高", "在火绒界面退出或停止防护", "安全软件关闭后系统将失去实时防护");
        Add(["avp", "avpui"], "卡巴斯基", "卡巴斯基杀毒主进程",
            K, "安全防护", "高", Exit);
        Add(["mcafee", "mcshield", "mcapexe"], "McAfee", "迈克菲安全软件",
            K, "安全防护", "高", Exit);
        Add(["ccsvchst"], "Norton", "诺顿安全软件服务宿主",
            K, "安全防护", "高", Svc);
        Add(["zhudongfangyu", "360tray", "360safe", "360sd", "360rp"], "360 安全卫士/杀毒", "360 的安全防护与托盘程序",
            K, "安全防护", "高", "在 360 界面退出防护；不建议直接结束", "强行结束可能触发驱动保护");
    }

    // ---------------------------------------------------------------- 第三方软件
    private static void SeedThirdParty()
    {
        // 通讯
        Add(["wechat", "weixin", "wechatappex"], "腾讯微信", "微信主程序/小程序容器",
            S, "即时通讯", "低", Exit, "关闭后收不到新消息");
        Add(["qq", "qqsclauncher", "qqprotect"], "腾讯 QQ", "QQ 主程序与安全防护组件",
            S, "即时通讯", "低", Exit);
        Add(["tim"], "腾讯 TIM", "TIM 办公版 QQ",
            S, "即时通讯", "低", Exit);
        Add(["wxwork"], "企业微信", "企业微信客户端",
            S, "即时通讯", "低", Exit);
        Add(["dingtalk"], "钉钉", "钉钉客户端（含更新组件）",
            S, "即时通讯", "低", Exit);
        Add(["feishu", "lark"], "飞书", "飞书客户端",
            S, "即时通讯", "低", Exit);
        Add(["aliwangwang"], "阿里旺旺", "淘宝/1688 卖家买家沟通工具",
            S, "即时通讯", "低", Exit);
        Add(["telegram"], "Telegram", "Telegram 客户端",
            S, "即时通讯", "低", Exit);
        Add(["discord"], "Discord", "Discord 语音与社群客户端",
            S, "即时通讯", "低", Exit);
        Add(["skype"], "Skype", "Skype 通话客户端",
            S, "即时通讯", "低", Exit);
        Add(["ms-teams", "teams"], "Microsoft Teams", "Teams 会议与协作客户端",
            S, "办公软件", "低", Exit);
        Add(["zoom"], "Zoom", "Zoom 视频会议",
            S, "办公软件", "低", Exit);
        Add(["slack"], "Slack", "Slack 团队协作",
            S, "办公软件", "低", Exit);
        Add(["whatsapp"], "WhatsApp", "WhatsApp 桌面客户端",
            S, "即时通讯", "低", Exit);

        // 办公
        Add(["wps", "et", "wpp", "wpspdf"], "金山 WPS Office", "WPS 文字/表格/演示/PDF",
            S, "办公软件", "低", "保存文档后关闭窗口", "未保存内容会丢失");
        Add(["wpscloudsvr", "wpscenter", "wpsupdate", "ksomisc"], "金山 WPS Office", "WPS 云文档同步、WPS 中心与更新组件",
            S, "办公软件", "低", "在 WPS 设置中关闭云同步/自动更新后可从任务管理器结束");
        Add(["winword"], "Microsoft Word", "Word 文档编辑",
            S, "办公软件", "低", "保存后关闭窗口");
        Add(["excel"], "Microsoft Excel", "Excel 表格",
            S, "办公软件", "低", "保存后关闭窗口");
        Add(["powerpnt"], "Microsoft PowerPoint", "PowerPoint 演示文稿",
            S, "办公软件", "低", "保存后关闭窗口");
        Add(["outlook"], "Microsoft Outlook", "Outlook 邮件与日历",
            S, "办公软件", "低", Exit);
        Add(["onenote"], "Microsoft OneNote", "OneNote 笔记",
            S, "办公软件", "低", Exit);
        Add(["msaccess"], "Microsoft Access", "Access 数据库",
            S, "办公软件", "低", "保存后关闭窗口");
        Add(["acrobat", "acrord32"], "Adobe Acrobat / Reader", "PDF 阅读与编辑",
            S, "办公软件", "低", Exit);
        Add(["photoshop"], "Adobe Photoshop", "图像处理",
            S, "办公软件", "低", "保存后退出");
        Add(["illustrator"], "Adobe Illustrator", "矢量绘图",
            S, "办公软件", "低", "保存后退出");
        Add(["creative cloud", "creativecloud"], "Adobe Creative Cloud", "Adobe 软件管理与云同步",
            S, "办公软件", "低", Exit);

        // 浏览器
        Add(["chrome"], "Google Chrome", "Chrome 浏览器（每个标签页/扩展各一个进程）",
            S, "浏览器", "低", "关闭对应标签页或整个浏览器");
        Add(["msedge"], "Microsoft Edge", "Edge 浏览器",
            S, "浏览器", "低", "关闭对应标签页或整个浏览器");
        Add(["firefox"], "Mozilla Firefox", "Firefox 浏览器",
            S, "浏览器", "低", Exit);
        Add(["opera"], "Opera", "Opera 浏览器",
            S, "浏览器", "低", Exit);
        Add(["iexplore"], "Microsoft Internet Explorer", "IE 浏览器（部分老系统/内网系统仍使用）",
            S, "浏览器", "低", Exit);
        Add(["360se", "360chrome"], "360 浏览器", "360 安全/极速浏览器",
            S, "浏览器", "低", Exit);
        Add(["qqbrowser"], "QQ 浏览器", "QQ 浏览器",
            S, "浏览器", "低", Exit);
        Add(["sogouexplorer"], "搜狗浏览器", "搜狗高速浏览器",
            S, "浏览器", "低", Exit);

        // 影音/下载
        Add(["cloudmusic"], "网易云音乐", "网易云音乐客户端",
            S, "影音播放", "低", Exit);
        Add(["qqmusic"], "QQ 音乐", "QQ 音乐客户端",
            S, "影音播放", "低", Exit);
        Add(["kugou"], "酷狗音乐", "酷狗音乐客户端",
            S, "影音播放", "低", Exit);
        Add(["iqiyi", "qyclient", "qyupdate"], "爱奇艺", "爱奇艺客户端与更新组件",
            S, "影音播放", "低", Exit);
        Add(["youku"], "优酷", "优酷客户端",
            S, "影音播放", "低", Exit);
        Add(["bilibili"], "哔哩哔哩", "哔哩哔哩客户端",
            S, "影音播放", "低", Exit);
        Add(["douyin"], "抖音", "抖音电脑版",
            S, "影音播放", "低", Exit);
        Add(["potplayer", "potplayermini64", "potplayermini"], "PotPlayer", "本地视频播放器",
            S, "影音播放", "低", Exit);
        Add(["vlc"], "VLC", "VLC 播放器",
            S, "影音播放", "低", Exit);
        Add(["wmplayer"], "Windows Media Player", "系统自带播放器",
            S, "影音播放", "低", Exit);
        Add(["baidunetdisk"], "百度网盘", "百度网盘同步/下载客户端",
            S, "下载工具", "低", Exit);
        Add(["thunder", "xunlei", "thunderplatform"], "迅雷", "迅雷下载客户端与下载引擎",
            S, "下载工具", "低", Exit);
        Add(["nutstore"], "坚果云", "坚果云同步客户端",
            S, "下载工具", "低", Exit);

        // 远程控制
        Add(["sunloginclient", "sunloginremote"], "向日葵远程控制", "向日葵被控/主控端",
            K, "远程控制", "高", "在本机向日葵界面退出；被远程控制时结束会中断连接",
            "如果不是你安装的，请立即检查是否被他人远程控制");
        Add(["todesk"], "ToDesk", "ToDesk 远程控制",
            K, "远程控制", "高", "在 ToDesk 界面退出", "来源不明时警惕未授权远程访问");
        Add(["anydesk"], "AnyDesk", "AnyDesk 远程控制",
            K, "远程控制", "高", "在 AnyDesk 界面退出");
        Add(["teamviewer"], "TeamViewer", "TeamViewer 远程控制",
            K, "远程控制", "高", "在 TeamViewer 界面退出");

        // 输入法
        Add(["sogoucloud", "sogouimebs", "sogoupinyin"], "搜狗输入法", "搜狗拼音输入法与云输入组件",
            S, "输入法", "低", "在输入法设置中退出；结束后可切换其他输入法");
        Add(["qqpinyin"], "QQ 拼音", "QQ 拼音输入法",
            S, "输入法", "低", Exit);
        Add(["baidupinyin"], "百度输入法", "百度拼音输入法",
            S, "输入法", "低", Exit);

        // 游戏平台
        Add(["steam", "steamwebhelper"], "Steam", "Steam 客户端与内嵌浏览器",
            S, "游戏平台", "低", "在 Steam 菜单选择“退出”");
        Add(["epicgameslauncher"], "Epic Games", "Epic 游戏平台",
            S, "游戏平台", "低", Exit);
        Add(["battle.net"], "暴雪战网", "暴雪游戏平台",
            S, "游戏平台", "低", Exit);

        // 压缩
        Add(["winrar"], "WinRAR", "压缩/解压工具",
            S, "压缩工具", "低", Exit);
        Add(["7zfm", "7zg"], "7-Zip", "7-Zip 压缩工具",
            S, "压缩工具", "低", Exit);
        Add(["bandizip"], "Bandizip", "压缩工具",
            S, "压缩工具", "低", Exit);
        Add(["haozip"], "好压", "国产压缩工具",
            S, "压缩工具", "低", Exit);

        // 同步盘
        Add(["onedrive"], "Microsoft OneDrive", "OneDrive 云同步",
            S, "云同步", "低", "右键托盘图标 → 退出");
        Add(["dropbox"], "Dropbox", "Dropbox 同步客户端",
            S, "云同步", "低", Exit);
        Add(["googledrivefs"], "Google Drive", "Google 云端硬盘",
            S, "云同步", "低", Exit);
        Add(["icloud"], "iCloud", "苹果 iCloud 同步",
            S, "云同步", "低", Exit);
    }

    // ---------------------------------------------------------------- 开发/数据库
    private static void SeedDevTools()
    {
        Add(["code"], "Visual Studio Code", "代码编辑器（每个窗口/扩展宿主各一个进程）",
            S, "开发工具", "低", "关闭窗口或“文件 → 退出”");
        Add(["devenv"], "Microsoft Visual Studio", "Visual Studio IDE",
            S, "开发工具", "低", "保存后关闭；调试中的程序会一起结束");
        Add(["rider64"], "JetBrains Rider", ".NET IDE",
            S, "开发工具", "低", Exit);
        Add(["idea64"], "JetBrains IntelliJ IDEA", "Java IDE",
            S, "开发工具", "低", Exit);
        Add(["pycharm64"], "JetBrains PyCharm", "Python IDE",
            S, "开发工具", "低", Exit);
        Add(["webstorm64"], "JetBrains WebStorm", "前端 IDE",
            S, "开发工具", "低", Exit);
        Add(["goland64"], "JetBrains GoLand", "Go IDE",
            S, "开发工具", "低", Exit);
        Add(["notepad++"], "Notepad++", "文本编辑器",
            S, "开发工具", "低", "保存后关闭");
        Add(["sublime_text"], "Sublime Text", "文本编辑器",
            S, "开发工具", "低", "保存后关闭");
        Add(["node"], "Node.js", "JavaScript 运行时（前端构建、脚本、Electron 应用）",
            C, "运行时", "中", "先停止对应开发服务器/脚本；直接结束可能留下占用端口的进程");
        Add(["java", "javaw"], "Java 运行时", "Java 应用或服务（如 Tomcat、IDEA 构建、部分企业客户端）",
            C, "运行时", "中", "确认宿主程序后，从其界面退出或停止对应服务");
        Add(["python", "pythonw"], "Python", "Python 脚本解释器",
            C, "运行时", "中", "确认脚本用途后再结束；可能是后台任务或服务");
        Add(["dotnet"], ".NET 运行时", ".NET 应用宿主（本程序自身也是 dotnet 应用）",
            C, "运行时", "中", "确认宿主程序后关闭对应窗口");
        Add(["msbuild"], "MSBuild", "Visual Studio 的构建引擎",
            C, "开发工具", "中", "等构建结束；卡住时可结束，下一次构建会重启");
        Add(["git"], "Git", "Git 命令行（拉取/提交/钩子脚本）",
            C, "开发工具", "中", "等命令执行完；卡在交互式提示时可直接结束");
        Add(["docker desktop", "docker", "com.docker.backend", "vpnkit"], "Docker Desktop", "Docker 容器环境（WSL/Hyper-V 后端）",
            C, "开发工具", "中", "在 Docker Desktop 界面选择 Quit；直接结束容器可能异常退出");
        Add(["vmmem", "vmmemwsl", "wslservice", "wslhost"], "Windows Subsystem for Linux", "WSL 虚拟机内存进程",
            C, "运行时", "中", "在命令行执行 wsl --shutdown 正常关闭");
        Add(["ssms"], "SQL Server Management Studio", "SQL Server 管理工具",
            S, "开发工具", "低", "保存查询后关闭");
        Add(["sqlservr"], "Microsoft SQL Server", "SQL Server 数据库引擎服务",
            K, "数据库", "高", "services.msc → SQL Server (实例名) → 停止",
            "直接结束会导致数据库进入恢复状态，生产库严禁直接结束");
        Add(["mysqld"], "MySQL", "MySQL 数据库服务",
            K, "数据库", "高", "net stop MySQL 或 services.msc 停止", "直接结束可能损坏数据");
        Add(["oracle"], "Oracle Database", "Oracle 数据库实例",
            K, "数据库", "高", "使用 Oracle 服务或 sqlplus shutdown 正常关闭", "严禁直接结束");
        Add(["redis-server"], "Redis", "Redis 缓存服务",
            C, "数据库", "中", "执行 redis-cli shutdown 正常关闭");
        Add(["navicat"], "Navicat", "数据库管理客户端",
            S, "开发工具", "低", Exit);
        Add(["postman"], "Postman", "API 调试工具",
            S, "开发工具", "低", Exit);
        Add(["xshell"], "Xshell", "SSH 终端工具",
            S, "开发工具", "低", Exit);
        Add(["xftp"], "Xftp", "SFTP 文件传输",
            S, "开发工具", "低", Exit);
        Add(["putty"], "PuTTY", "SSH 客户端",
            S, "开发工具", "低", Exit);
        Add(["filezilla"], "FileZilla", "FTP/SFTP 客户端",
            S, "开发工具", "低", Exit);
        Add(["winget", "setup", "installer"], "软件安装程序", "正在安装/卸载软件",
            C, "安装程序", "中", "安装过程中不要结束，否则可能残留损坏的软件");
        Add(["sldworks"], "SolidWorks", "三维 CAD 设计软件",
            S, "工业软件", "低", "保存后退出", "未保存的模型会丢失");
        Add(["swbackgrounddownload"], "SolidWorks", "SolidWorks 后台下载/更新组件",
            S, "工业软件", "低", Exit);
        Add(["acad"], "AutoCAD", "二维/三维 CAD",
            S, "工业软件", "低", "保存后退出");
        Add(["zw3d"], "中望 ZW3D", "三维 CAD/CAM 软件",
            S, "工业软件", "低", "保存后退出");
    }

    // ---------------------------------------------------------------- 驱动/硬件/系统工具
    private static void SeedHardwareAndTools()
    {
        Add(["nvcontainer", "nvsphelper64", "nvdisplay.container", "nvtelemetrycontainer", "nvwebhelper", "nvtray"],
            "NVIDIA 显卡驱动", "显卡驱动服务、控制面板与遥测组件",
            C, "驱动程序", "中", "在 NVIDIA 控制面板/GeForce Experience 中关闭相关功能；不建议直接结束");
        Add(["amdow", "radeonsoftware", "cnext", "atiesrxx"], "AMD 显卡驱动", "AMD 显卡驱动与控制面板",
            C, "驱动程序", "中", "不建议直接结束；在黑屏/驱动异常时可重启相关服务");
        Add(["igfxem", "igfxtray", "intelcphdcpsvc", "dpmservice"], "Intel 显卡驱动", "Intel 核显驱动与显示服务",
            C, "驱动程序", "中", "不建议直接结束");
        Add(["rtkauduservice64", "ravbg64", "rtkngui64"], "Realtek 声卡驱动", "Realtek 音频驱动服务与音效面板",
            C, "驱动程序", "中", "结束后可能没有声音，重启或重启服务可恢复");
        Add(["logioptionsplus_agent", "logioptionsplus_updater", "logivoice", "lghub"],
            "罗技 Logitech 驱动", "罗技鼠标/键盘的高级功能与宏（Options+ / G HUB）",
            C, "驱动程序", "低", "在 Logi Options+ / G HUB 界面退出；退出后鼠标键盘基础功能不受影响",
            "其更新服务会持续下载大体积安装包");
        Add(["syntpenh", "syntpenhservice"], "Synaptics 触摸板驱动", "笔记本触摸板手势与设置",
            C, "驱动程序", "中", "不建议结束，否则触摸板手势失效");
        // 系统工具/网络工具
        Add(["clash-verge", "clash-verge-service", "verge-mihomo", "clash-meta"], "Clash Verge",
            "代理/VPN 客户端或其内核，负责转发网络请求",
            S, "网络工具", "中", "在 Clash Verge 界面退出；退出后代理规则失效", "来源不明时请确认不是他人安装");
        Add(["crashpad_handler"], "Chromium/Electron 崩溃处理器", "替宿主程序收集崩溃转储的子进程，随软件一同退出",
            S, "运行库", "低", "关闭对应软件即可；单独结束会被宿主重新拉起");
        Add(["everything"], "Everything", "本地文件极速搜索工具",
            S, "系统工具", "低", Exit);
        Add(["snipaste"], "Snipaste", "截图/贴图工具",
            S, "系统工具", "低", "右键托盘图标 → 退出");
        Add(["pixpin"], "PixPin", "截图/贴图/OCR 工具",
            S, "系统工具", "低", "右键托盘图标 → 退出");
    }
}
