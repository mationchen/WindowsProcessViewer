namespace WindowsProcessViewer.Models;

/// <summary>appsettings.json 的根对象。</summary>
public sealed class AppSettings
{
    public MiniMaxSettings MiniMax { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
}

/// <summary>MiniMax AI 接入参数。ApiKey 留空表示不启用 AI。</summary>
public sealed class MiniMaxSettings
{
    public string ApiKey { get; set; } = "";
    public string Endpoint { get; set; } = "https://api.minimaxi.com/v1/chat/completions";
    public string Model { get; set; } = "MiniMax-M3.1-Flash";
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxCompletionTokens { get; set; } = 4096;
    public int MaxProcessesPerRequest { get; set; } = 12;
    public string ReasoningEffort { get; set; } = "";
    public bool DisableThinking { get; set; }
}

/// <summary>界面默认值（只在启动时生效，运行中改界面不会回写）。</summary>
public sealed class UiSettings
{
    public bool OnlyThirdParty { get; set; }
    public bool OnlyRisky { get; set; }
    public bool HideMicrosoftProcesses { get; set; }
    public bool ShowKillMenu { get; set; } = true;
    public int AutoRefreshSeconds { get; set; }
    /// <summary>启动时放到第几个显示器（Screen.AllScreens 顺序，从 1 开始；0 = 系统默认）。</summary>
    public int StartScreen { get; set; }
    public string FontName { get; set; } = "Microsoft YaHei UI";
    public float FontSize { get; set; } = 9f;
}
