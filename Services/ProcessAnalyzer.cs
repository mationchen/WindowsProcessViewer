using WindowsProcessViewer.Models;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 把采集到的原始信息翻译成用户能看懂的结论：
/// 所属软件、作用、谁启动的（具体程序 + 进程链 + 依据）、能不能关/怎么关。
/// 顺序：内置知识库 → SQLite 缓存 → 规则推断；AI 结论从缓存直接带出，避免重复花钱。
/// </summary>
public sealed class ProcessAnalyzer(bool selfElevated, AppCache? cache = null)
{
    public void AnalyzeAll(IEnumerable<ProcessRecord> records)
    {
        var list = records as IReadOnlyList<ProcessRecord> ?? records.ToList();
        var byPid = new Dictionary<int, ProcessRecord>(list.Count);
        foreach (var record in list)
            byPid[record.Pid] = record;

        foreach (var record in list)
            Analyze(record, byPid);
    }

    public void Analyze(ProcessRecord record, IReadOnlyDictionary<int, ProcessRecord> byPid)
    {
        // 1) 谁启动的：命令行宿主线索 → 父进程 → 服务/系统
        var launch = LauncherResolver.Resolve(record, byPid);
        record.LaunchedBy = launch.Launcher;
        record.LaunchChain = launch.Chain;
        record.LaunchEvidence = launch.Evidence;
        record.CommandLineSummary = launch.CommandLineSummary;

        // 2) 所属软件 / 作用 / 能否关闭
        var knowledge = KnowledgeBase.Find(record.Name);
        if (knowledge is not null)
        {
            record.Software = knowledge.Software;
            record.Purpose = knowledge.Purpose;
            record.CanClose = AdviceText(knowledge.Advice);
            record.CloseMethod = string.IsNullOrWhiteSpace(knowledge.CloseMethod)
                ? DefaultCloseMethod(knowledge.Advice)
                : knowledge.CloseMethod;
            record.Category = knowledge.Category;
            record.Risk = knowledge.Risk;
            record.Notes = knowledge.Notes;
            record.AnalyzeSource = "内置知识库";
        }
        else
        {
            ApplyCachedOrHeuristic(record);
        }

        ApplyOverrides(record);
        record.Notes = BuildNotes(record);

        // 3) AI 结论：先看缓存（同一应用的判断已经算过就不再花钱）
        PrefillAiFromCache(record);

        // 4) 回写缓存（仅在内容变化时才真正落盘）
        cache?.UpdateFromRecord(record);
    }

    private void ApplyCachedOrHeuristic(ProcessRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.CacheKey))
            record.CacheKey = AppCache.BuildKey(record.Path, record.Name);

        var cached = cache?.Find(record.CacheKey);
        if (cached is not null &&
            !string.IsNullOrWhiteSpace(cached.Software) &&
            !string.IsNullOrWhiteSpace(cached.CanClose))
        {
            record.Software = cached.Software;
            record.Purpose = cached.Purpose;
            record.CanClose = cached.CanClose;
            record.CloseMethod = cached.CloseMethod;
            record.Category = cached.Category;
            record.Risk = cached.Risk;
            record.AnalyzeSource = string.IsNullOrWhiteSpace(cached.Source)
                ? "缓存"
                : cached.Source.Contains("缓存", StringComparison.Ordinal) ? cached.Source : cached.Source + "（缓存）";
            return;
        }

        ApplyHeuristic(record);
    }

    private void PrefillAiFromCache(ProcessRecord record)
    {
        var cached = cache?.Find(record.CacheKey);
        if (cached is null || !cached.HasAi)
            return;

        record.AiSoftware = cached.AiSoftware;
        record.AiPurpose = cached.AiPurpose;
        record.AiLaunchedBy = cached.AiLaunchedBy;
        record.AiCanClose = cached.AiCanClose;
        record.AiCloseMethod = cached.AiCloseMethod;
        record.AiRisk = cached.AiRisk;
        record.AiSummary = cached.AiSummary;
        record.AiDetail = cached.AiDetail;
        record.AiFromCache = true;
    }

    /// <summary>知识库结论在特定情况下需要降级：以服务方式运行的进程不适合“直接结束”。</summary>
    private static void ApplyOverrides(ProcessRecord record)
    {
        if (record.IsService && record.CanClose == AdviceText(CloseAdvice.Safe))
        {
            record.CanClose = AdviceText(CloseAdvice.Caution);
            record.CloseMethod = "不要直接结束：该进程正在承载 Windows 服务，请用 services.msc 停止对应服务";
            record.Risk = "中";
        }

        if (!string.IsNullOrEmpty(record.ServiceDisplayName) && record.CanClose == AdviceText(CloseAdvice.Safe))
            record.CloseMethod = $"所属服务：{record.ServiceDisplayName}；" + record.CloseMethod;
    }

    private static void ApplyHeuristic(ProcessRecord record)
    {
        var company = record.Company;
        var product = record.ProductName;
        var description = record.FileDescription;
        var path = record.Path;
        var microsoft = company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                        record.SignatureStatus.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);
        var inWindowsFolder = IsUnderWindows(path);

        if (string.IsNullOrWhiteSpace(path))
        {
            record.Software = microsoft ? "Microsoft Windows" : "未知程序（无法读取文件路径，权限不足）";
            record.Purpose = string.IsNullOrWhiteSpace(description) ? "无法获取程序说明，建议以管理员身份重新运行本工具查看" : description;
            record.Category = "未知";
            record.CanClose = AdviceText(CloseAdvice.Caution);
            record.CloseMethod = "先以管理员身份运行本工具确认来源，再决定是否结束";
            record.Risk = "中";
            record.AnalyzeSource = "规则推断";
            return;
        }

        var unsigned = record.SignatureStatus == "无数字签名";
        var suspiciousLocation =
            path.Contains(@"\AppData\Local\Temp\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Windows\Temp\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase) ||
            path.Contains(@"\ProgramData\Temp\", StringComparison.OrdinalIgnoreCase);

        if (microsoft || inWindowsFolder)
        {
            record.Software = string.IsNullOrWhiteSpace(product) ? "Microsoft Windows" : product;
            record.Purpose = string.IsNullOrWhiteSpace(description) ? "Windows 系统组件" : description;
            record.Category = "Windows 组件";
            record.CanClose = AdviceText(CloseAdvice.Caution);
            record.CloseMethod = "确认不是系统关键服务后，用任务管理器结束；服务类进程请用 services.msc 停止服务";
            record.Risk = "中";
            record.AnalyzeSource = "规则推断（系统目录）";
        }
        else if (unsigned && suspiciousLocation)
        {
            record.Software = string.IsNullOrWhiteSpace(company) ? "未知（无签名，位于临时/下载目录）" : company;
            record.Purpose = string.IsNullOrWhiteSpace(description) ? "无文件说明，且无数字签名，位置可疑" : description;
            record.Category = "可疑程序";
            record.CanClose = AdviceText(CloseAdvice.Caution);
            record.CloseMethod = "先确认来源：可结束进程并做杀毒扫描；若是自己运行的安装包/脚本请核对路径";
            record.Risk = "高";
            record.AnalyzeSource = "规则推断（可疑路径）";
        }
        else
        {
            record.Software = ChooseSoftware(product, company, path);
            record.Purpose = string.IsNullOrWhiteSpace(description) ? "（文件未提供描述信息）" : description;
            record.Category = "第三方软件";
            record.CanClose = AdviceText(CloseAdvice.Safe);
            record.CloseMethod = "在该软件界面选择“退出”；或任务管理器结束任务";
            record.Risk = unsigned ? "中" : "低";
            record.AnalyzeSource = "规则推断（文件信息）";
        }
    }

    /// <summary>
    /// 优先用产品名；但部分程序的产品名是“(x86)”这类无意义值，此时退回公司名，再退回路径里的厂商标。
    /// </summary>
    private static string ChooseSoftware(string product, string company, string path)
    {
        var productName = (product ?? "").Trim();
        if (productName.Length > 2 && !productName.StartsWith('('))
            return productName;

        var companyName = (company ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(companyName))
            return companyName;

        return GuessVendorFromPath(path);
    }

    private static bool IsUnderWindows(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrEmpty(windows) &&
               path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GuessVendorFromPath(string path)
    {
        try
        {
            // C:\Program Files\Vendor\... → Vendor
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            foreach (var root in new[] { programFiles, programFilesX86 })
            {
                if (string.IsNullOrEmpty(root) || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                var rest = path[root.Length..].TrimStart('\\');
                var index = rest.IndexOf('\\');
                if (index > 0)
                    return rest[..index];
            }
        }
        catch
        {
            // 路径解析失败时退回通用文案
        }

        return "未知厂商";
    }

    private string BuildNotes(ProcessRecord record)
    {
        var notes = new List<string>();
        if (!string.IsNullOrWhiteSpace(record.Notes))
            notes.Add(record.Notes.TrimEnd('。', '；'));

        if (!string.IsNullOrWhiteSpace(record.SignatureStatus))
            notes.Add(record.SignatureStatus);

        if (record.IsElevated)
        {
            notes.Add(selfElevated
                ? "该进程以管理员权限运行"
                : "该进程以管理员权限运行，本工具未提权，结束它会失败");
        }

        if (string.IsNullOrWhiteSpace(record.Owner))
            notes.Add("部分信息因权限不足不可见（以管理员身份运行本工具可改善）");

        if (!string.IsNullOrWhiteSpace(record.Autostart))
            notes.Add($"开机自启：{record.Autostart}");

        if (record.Category == "可疑程序")
            notes.Add("位置或签名可疑，建议核对来源并做安全扫描");

        return string.Join("；", notes.Where(n => !string.IsNullOrWhiteSpace(n))) + "。";
    }

    public static string AdviceText(CloseAdvice advice) => advice switch
    {
        CloseAdvice.Safe => "可以关闭",
        CloseAdvice.Caution => "谨慎关闭",
        CloseAdvice.Keep => "建议保留",
        CloseAdvice.Forbidden => "禁止关闭",
        _ => "未知",
    };

    public static string DefaultCloseMethod(CloseAdvice advice) => advice switch
    {
        CloseAdvice.Safe => "在该软件界面选择“退出”；或任务管理器结束任务",
        CloseAdvice.Caution => "确认没有未保存的工作后，用任务管理器结束任务",
        CloseAdvice.Keep => "不建议关闭；确需关闭请用 services.msc 停止对应服务",
        CloseAdvice.Forbidden => "无法关闭：系统核心进程，只能关机/重启时终止",
        _ => "",
    };
}
