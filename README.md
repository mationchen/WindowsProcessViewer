# Windows 进程查看器（WindowsProcessViewer）

本机用的 Windows 桌面小工具（.NET 9 + WinForms）。把当前所有进程列出来，并直接回答五个问题：
**它属于哪个软件、干什么用、是谁启动的、能不能关、怎么关。** 可选接入 MiniMax AI 做二次判断。

## 运行

```powershell
# 直接运行已编译版本（需 .NET 9 桌面运行时，本机已有）
C:\Extech\ExtWebsites\WindowsProcessViewer\bin\Release\net9.0-windows\WindowsProcessViewer.exe

# 指定显示器打开（Screen 顺序从 1 开始；本机 1=副屏 1920x1080，2=主屏 3440x1440）
.\bin\Release\net9.0-windows\WindowsProcessViewer.exe --screen 1

# AI 自检：验证 Key / 区域 / 模型，并真跑一次分析（结果同时写入缓存）
.\bin\Release\net9.0-windows\WindowsProcessViewer.exe --ai-test

# 无界面导出 CSV（排查/留档，退出码 0 表示成功）
.\bin\Release\net9.0-windows\WindowsProcessViewer.exe --export D:\procs.csv

# 从源码运行
cd C:\Extech\ExtWebsites\WindowsProcessViewer
dotnet run --project WindowsProcessViewer.csproj
```

窗口位置还可在配置里固定：`Ui.StartScreen`（`--screen` 优先级更高）。

## 功能对照

| 需求 | 实现 |
| --- | --- |
| 列出所有进程 | 主表格 14 列：进程名、PID、所属软件、作用说明、**谁启动的**、能否关闭、如何关闭、风险、内存、CPU、运行账户、类别、AI 结论、**命令行** |
| 标明属于什么软件 / 什么作用 | 内置知识库（Windows 自带 + 常见国内外软件 150+ 条）优先；未命中按厂商名、产品名、文件描述、路径、签名规则推断，并标注“判断来源” |
| **谁启动的（哪个程序）** | 先解析命令行的宿主线索（`--webview-exe-name=`、`--user-data-dir=`、`--app-path=`），再退回父进程链；选中后“详细信息”里给出完整进程链和判断依据。例如 `msedgewebview2.exe → clash-verge.exe（Clash Verge）` |
| 能不能关掉 | 四级：可以关闭 / 谨慎关闭 / 建议保留 / 禁止关闭 |
| 如何关掉 | 软件内退出 / 任务管理器结束 / services.msc 停止某服务 / 系统核心不可关闭 |
| MiniMax AI 辅助 | “AI 分析选中”“AI 分析当前列表”，结果落在“AI 结论”列与“AI 分析”标签页，**不覆盖**内置结论 |
| 不重复查询 | 所有应用的文件信息、判断结论、AI 结论写入本地 SQLite（`cache.db`），下次直接命中 |

## 配置文件（重要）

配置**不在 exe 目录**，而是固定放在：

```
%APPDATA%\WindowsProcessViewer\appsettings.json
（本机：C:\Users\Administrator\AppData\Roaming\WindowsProcessViewer\appsettings.json）
```

程序里点“打开配置文件”会用记事本打开这个真实路径。仓库里的 `appsettings.json` 只是模板；
需要换位置时可设环境变量 `WPV_CONFIG` 指向任意文件。改完点“重新加载配置”。

```jsonc
"MiniMax": {
  "ApiKey": "",                                            // ← 在这里填你的 Key
  "Endpoint": "https://api.minimaxi.com/v1/chat/completions",
  "Model": "MiniMax-M3.1-Flash"
}
```

- 国内账号用 `https://api.minimaxi.com/v1/chat/completions`，国际账号用 `https://api.minimax.io/v1/chat/completions`；Key 与区域必须匹配，否则 401。
- 也可用环境变量 `MINIMAX_API_KEY`（优先级高于配置文件）。
- `MiniMax-M3.1-Flash` 不支持关闭思考，`DisableThinking: true` 会返回 400；`ReasoningEffort` 留空则不发送该字段。
- 未填 Key 时，除 AI 外的功能全部可用。

## 本地缓存（SQLite）

- 位置：exe 同目录 `cache.db`（WAL 模式），键是**可执行文件路径**，读不到路径时用进程名。
- 存什么：厂商/产品/版本/数字签名、判断结论（软件/作用/能否关闭/如何关闭/风险）、AI 结论、出现次数与首末次时间。
- 何时失效：文件大小或最后写入时间变化时重新读取版本与签名；换版本不会丢 AI 结论。
- 何时写盘：只在“首次见到该应用”“文件指纹变化”“AI 有新结果”时写，一次刷新不会产生几百条 SQL。
- 启动时整表读入内存，之后判断都在内存里；缓存损坏/被占用会自动降级为纯内存模式，不影响进程列表。
- 顶部“缓存 N”按钮：查看数量、清空缓存（清空后重新分析，AI 结论也会丢失）。

实测：冷启动导出约 2.4 秒，缓存命中后约 0.9 秒。

## 界面要点

- 点列头排序：表头会显示 **▲ 升序 / ▼ 降序**（同时状态栏提示“已按 XX 降序排列”）。
- 搜索框匹配进程名 / PID / 厂商 / 路径 / 命令行 / 账户；三个筛选开关（只看第三方、只看可疑/高风险、隐藏系统进程）。
- **关闭建议筛选**：一行四个可多选的勾选框（可以关闭 / 谨慎关闭 / 建议保留 / 禁止关闭），勾几个就只显示这几类；全不勾表示不限。
- 下方“详细信息”含文件路径、完整命令行、运行账户、数字签名、开机自启来源、所属服务、进程链。
- 右键菜单：复制详细信息、打开文件所在位置、结束进程（系统核心进程已禁止）、打开“服务”管理器。
- F5 刷新；自动刷新 5 秒 ~ 5 分钟可选。
- AI 批量分析时，状态栏右侧显示**进度条 + “已处理/总数（百分比）”**，不再是鼠标转圈。
- **首次刷新 CPU% 显示 0 属正常**（需要前后两次采样）。
- 想看受保护进程的完整信息，请“以管理员身份运行”。

## 已知限制

- “能否关闭”是基于经验规则 + 文件信息的判断，AI 结论也仅供参考；重要程序请在软件自身界面退出并保存。
- 数字签名只做本地 WinVerifyTrust 校验（含目录签名），不联网做吊销检查。
- 开机自启只扫 `Run` 键与启动文件夹，不含计划任务；服务自启通过 WMI 服务表体现。
- 普通权限下部分进程的路径 / 命令行 / 账户读不到，界面会写明“权限不足”，不是程序故障。
- “谁启动的”依赖命令行与父进程；父进程已退出且命令行无线索时会显示“无法确定”。
- 结束进程只在右键菜单里，且系统核心进程（csrss、lsass、winlogon 等）硬编码禁止。

## 目录结构

```
Program.cs                     入口；异常兜底；--export / --ai-test / --screen
Forms/MainForm.cs              全部界面（代码构建，无 designer 文件）
Models/ProcessRecord.cs        一行进程的完整数据（原始 + 结论 + 启动链 + AI）
Models/AppSettings.cs          appsettings.json 映射
Services/ProcessCollector.cs   采集：WMI + Process + P/Invoke + 签名 + 服务/自启 + 缓存接入
Services/ProcessAnalyzer.cs    结论生成：知识库 → 缓存 → 规则推断；AI 结果回填
Services/KnowledgeBase.cs      内置知识库（进程名 → 软件/作用/建议/关闭方法）
Services/LauncherResolver.cs   “谁启动的”：命令行宿主线索 + 父进程链 + 同名主进程
Services/AppCache.cs           SQLite 缓存（cache.db）
Services/WmiQuery.cs           Win32_Process / Win32_Service 一次性查询
Services/NativeProcess.cs      运行账户、是否提权（令牌 API）
Services/SignatureChecker.cs   WinVerifyTrust 签名校验
Services/ProcessTerminator.cs  结束进程的统一入口与保护规则
Services/AiService.cs          MiniMax OpenAI 兼容接口 + JSON 解析
Services/ConfigService.cs      配置读写（%APPDATA%）、默认值修正、Key 解析
Services/CsvExporter.cs        UTF-8 BOM CSV 导出
Services/SortableBindingList.cs 可排序绑定列表
appsettings.json               配置模板（运行时读取 %APPDATA% 那份）
```

日志：`Logs\WindowsProcessViewer_yyyyMMdd.log`（与 exe 同目录），记录异常与启动/采集摘要，**不记录 API Key**。
