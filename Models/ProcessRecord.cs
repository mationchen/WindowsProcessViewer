namespace WindowsProcessViewer.Models;

/// <summary>
/// 一个进程的完整展示数据：原始采集信息 + 内置知识库/规则判断 + AI 判断。
/// 直接作为 DataGridView 的绑定对象，属性名与列一一对应。
/// </summary>
public sealed class ProcessRecord
{
    // ---------- 原始采集 ----------
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public int ParentPid { get; set; }
    public string ParentName { get; set; } = "";
    public string Owner { get; set; } = "";
    public int SessionId { get; set; }
    public string Path { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public string Company { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string FileDescription { get; set; } = "";
    public string FileVersion { get; set; } = "";
    public string SignatureStatus { get; set; } = "";
    public DateTime? StartTime { get; set; }
    public double WorkingSetMb { get; set; }
    public double PrivateMb { get; set; }
    public double CpuPercent { get; set; }
    public int ThreadCount { get; set; }
    public bool IsElevated { get; set; }
    public bool IsService { get; set; }
    public string ServiceName { get; set; } = "";
    public string ServiceDisplayName { get; set; } = "";
    public bool ServiceAutoStart { get; set; }
    public string Autostart { get; set; } = "";
    public bool IsCurrentProcess { get; set; }

    /// <summary>SQLite 缓存键（路径优先），由采集器填写。</summary>
    public string CacheKey { get; set; } = "";

    // ---------- 判断结果（内置知识库 + 规则推断） ----------
    public string Software { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string LaunchedBy { get; set; } = "";
    public string LaunchChain { get; set; } = "";
    public string LaunchEvidence { get; set; } = "";
    public string CommandLineSummary { get; set; } = "";
    public string CanClose { get; set; } = "";
    public string CloseMethod { get; set; } = "";
    public string Category { get; set; } = "";
    public string Risk { get; set; } = "";
    public string Notes { get; set; } = "";
    public string AnalyzeSource { get; set; } = "";

    // ---------- AI 判断 ----------
    public string AiSoftware { get; set; } = "";
    public string AiPurpose { get; set; } = "";
    public string AiLaunchedBy { get; set; } = "";
    public string AiCanClose { get; set; } = "";
    public string AiCloseMethod { get; set; } = "";
    public string AiRisk { get; set; } = "";
    public string AiSummary { get; set; } = "";
    public string AiDetail { get; set; } = "";
    public bool AiFromCache { get; set; }

    public bool HasAi => !string.IsNullOrWhiteSpace(AiSummary) || !string.IsNullOrWhiteSpace(AiDetail);
}
