using System.Text;
using System.Text.Json;
using WindowsProcessViewer.Models;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 读取/写入 exe 同目录的 appsettings.json。
/// MiniMax.ApiKey 留空时，程序仍可正常使用内置知识库分析，只是 AI 功能不可用。
/// </summary>
public static class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>
    /// 配置固定放在 %APPDATA%\WindowsProcessViewer\appsettings.json。
    /// 放在用户目录而不是 exe 同目录，避免"改了源码里那份、程序读的是输出目录那份"的老问题，
    /// 也让 Key 不会随重新编译被覆盖。可用环境变量 WPV_CONFIG 指定其它路径。
    /// </summary>
    public static string ConfigPath { get; } = ResolveConfigPath();

    private static string ResolveConfigPath()
    {
        var overridden = Environment.GetEnvironmentVariable("WPV_CONFIG");
        if (!string.IsNullOrWhiteSpace(overridden))
            return overridden.Trim();

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "WindowsProcessViewer", "appsettings.json");
    }

    /// <summary>当前配置文件中的 API Key（支持用环境变量 MINIMAX_API_KEY 临时覆盖）。</summary>
    public static string ResolveApiKey(AppSettings settings)
    {
        var fromEnv = Environment.GetEnvironmentVariable("MINIMAX_API_KEY");
        return !string.IsNullOrWhiteSpace(fromEnv) ? fromEnv.Trim() : (settings.MiniMax.ApiKey ?? "").Trim();
    }

    public static AppSettings Load(out string? error)
    {
        error = null;
        try
        {
            var directory = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(ConfigPath))
            {
                // 迁移：如果 exe 旁边已有配置（老版本或便携用法），复制过来而不是从空模板开始
                var besideExe = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                if (File.Exists(besideExe) &&
                    !string.Equals(besideExe, ConfigPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(besideExe, ConfigPath);
                    Log.Info($"已把 exe 目录的配置迁移到 {ConfigPath}");
                }
                else
                {
                    File.WriteAllText(ConfigPath, DefaultJson, new UTF8Encoding(true));
                    Log.Info($"配置文件不存在，已生成默认配置：{ConfigPath}");
                    return JsonSerializer.Deserialize<AppSettings>(DefaultJson, Options) ?? new AppSettings();
                }
            }

            var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json))
            {
                error = $"配置文件内容为空，已使用默认配置。\n{ConfigPath}";
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            settings.MiniMax ??= new MiniMaxSettings();
            settings.Ui ??= new UiSettings();
            Normalize(settings);
            return settings;
        }
        catch (Exception ex)
        {
            error = $"配置文件读取失败：{ex.Message}\n{ConfigPath}";
            Log.Error("读取 appsettings.json 失败", ex);
            return new AppSettings();
        }
    }

    /// <summary>把缺省/非法值修正为可用值，避免用户手改配置后程序崩溃。</summary>
    private static void Normalize(AppSettings settings)
    {
        var m = settings.MiniMax;
        if (string.IsNullOrWhiteSpace(m.Endpoint))
            m.Endpoint = "https://api.minimaxi.com/v1/chat/completions";
        if (string.IsNullOrWhiteSpace(m.Model))
            m.Model = "MiniMax-M3.1-Flash";
        if (m.TimeoutSeconds is < 10 or > 600)
            m.TimeoutSeconds = 120;
        if (m.MaxCompletionTokens is < 256 or > 65536)
            m.MaxCompletionTokens = 4096;
        if (m.MaxProcessesPerRequest is < 1 or > 40)
            m.MaxProcessesPerRequest = 12;
        if (!string.IsNullOrWhiteSpace(m.Endpoint) && !Uri.TryCreate(m.Endpoint, UriKind.Absolute, out _))
            m.Endpoint = "https://api.minimaxi.com/v1/chat/completions";

        if (settings.Ui.FontSize is < 7f or > 20f)
            settings.Ui.FontSize = 9f;
        if (settings.Ui.StartScreen < 0)
            settings.Ui.StartScreen = 0;
        if (string.IsNullOrWhiteSpace(settings.Ui.FontName))
            settings.Ui.FontName = "Microsoft YaHei UI";
    }

    /// <summary>默认配置模板：带中文注释，方便直接填写 ApiKey。</summary>
    public const string DefaultJson = """
{
  // 说明：这只是模板。程序运行时读取 %APPDATA%\WindowsProcessViewer\appsettings.json
  // （首次启动会把本文件复制过去；也可用环境变量 WPV_CONFIG 指定其它路径）。

  // ===== MiniMax AI 配置（可选）=====
  // 在 ApiKey 中填入你自己的 MiniMax API Key，保存后点界面上的“重新加载配置”即可生效。
  // 也可以不改这里，改用环境变量 MINIMAX_API_KEY（优先级高于本文件）。
  "MiniMax": {
    "ApiKey": "",
    // 国内账号：https://api.minimaxi.com/v1/chat/completions
    // 国际账号：https://api.minimax.io/v1/chat/completions
    "Endpoint": "https://api.minimaxi.com/v1/chat/completions",
    // 可用模型：MiniMax-M3.1-Flash / MiniMax-M3 等，以控制台实际开通为准
    "Model": "MiniMax-M3.1-Flash",
    "TimeoutSeconds": 120,
    "MaxCompletionTokens": 4096,
    // 单次请求最多分析多少个进程（太大容易超时/超长）
    "MaxProcessesPerRequest": 12,
    // 思考深度：low / medium / high / xhigh / max；留空则用模型默认
    "ReasoningEffort": "low",
    // 仅 MiniMax-M3 支持关闭思考；M3.1-Flash 传 true 会返回 400，请保持 false
    "DisableThinking": false
  },

  // ===== 界面默认值 =====
  "Ui": {
    "OnlyThirdParty": false,
    "OnlyRisky": false,
    "HideMicrosoftProcesses": false,
    "ShowKillMenu": true,
    "AutoRefreshSeconds": 0,
    // 启动时放到第几个显示器（Screen 顺序，从 1 开始；0 = 系统默认）
    "StartScreen": 0,
    "FontName": "Microsoft YaHei UI",
    "FontSize": 9
  }
}
""";
}
