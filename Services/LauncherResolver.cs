using System.Text;
using System.Text.RegularExpressions;
using WindowsProcessViewer.Models;

namespace WindowsProcessViewer.Services;

/// <summary>“谁启动的”结论：启动程序 + 进程链 + 判断依据。</summary>
public sealed record LaunchInfo(string Launcher, string Chain, string Evidence, string CommandLineSummary);

/// <summary>
/// 推断"是谁启动了这个进程"。优先级：
/// 1) 命令行里的宿主线索（WebView2/Electron 常用 --webview-exe-name / --user-data-dir）；
/// 2) 父进程（Windows 记录的真实创建者）；
/// 3) 服务 / 开机自启 / 系统内核。
/// 之所以要解析命令行：像 msedgewebview2 这类子进程，父进程往往还是它自己，
/// 真正决定"属于哪个软件"的信息写在命令行参数里（任务管理器里也能看到）。
/// </summary>
public static class LauncherResolver
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edge"] = "Microsoft Edge",
        ["chrome"] = "Google Chrome",
        ["msedgewebview2"] = "Microsoft Edge WebView2",
        ["code"] = "Visual Studio Code",
        ["clash-verge"] = "Clash Verge",
        ["feishu"] = "飞书",
        ["lark"] = "飞书",
        ["wechat"] = "微信",
        ["weixin"] = "微信",
        ["dingtalk"] = "钉钉",
        ["wps"] = "WPS Office",
        ["qq"] = "QQ",
        ["steam"] = "Steam",
        ["obs-studio"] = "OBS Studio",
        ["postman"] = "Postman",
        ["notion"] = "Notion",
        ["slack"] = "Slack",
        ["discord"] = "Discord",
        ["telegram"] = "Telegram",
        ["spotify"] = "Spotify",
        ["cursor"] = "Cursor",
        ["claude"] = "Claude",
        ["squirrel"] = "Squirrel 更新器",
    };

    private static readonly HashSet<string> DataDirNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "User Data", "Default", "EBWebView", "Crashpad", "Temp", "Local", "Roaming", "AppData",
    };

    /// <summary>这些数据目录名太通用，无法据此判断宿主程序；命中时改为看父进程。</summary>
    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "cef", "chromium", "electron", "webview2", "msedgewebview2", "user data", "data",
        "cache", "desktop", "documents", "downloads", "temp", "appdata", "programdata", "common",
    };

    public static LaunchInfo Resolve(ProcessRecord record, IReadOnlyDictionary<int, ProcessRecord> byPid)
    {
        var chain = BuildChain(record, byPid);
        var summary = Truncate(record.CommandLine, 200);

        if (record.IsCurrentProcess)
            return new LaunchInfo("本工具自身（你手动启动）", chain, "当前进程", summary);

        if (record.Pid <= 4)
            return new LaunchInfo("系统内核（开机时创建）", chain, "PID ≤ 4", summary);

        if (record.IsService)
        {
            var display = string.IsNullOrWhiteSpace(record.ServiceDisplayName)
                ? record.ServiceName
                : record.ServiceDisplayName;
            var auto = record.ServiceAutoStart ? "，且随系统自动启动" : "";
            return new LaunchInfo($"Windows 服务「{display}」{auto}",
                chain,
                $"服务管理器 services.exe 依据 Win32_Service 启动（服务名 {record.ServiceName}）",
                summary);
        }

        // ---------- 1) 命令行里的宿主线索（自身 + 最多 3 层祖先） ----------
        var hint = FindHostHint(record, byPid, 3);
        if (hint is not null)
        {
            var described = DescribeProcess(hint.HostExeName, byPid);
            var baseName = hint.IsExeName ? StripExtension(hint.HostExeName) : StripExtension(described.ExeName);
            var hasRealExe = hint.IsExeName || described.Pid > 0;
            var label = FormatNode(baseName, hasRealExe, described.Software,
                hasRealExe ? described.Pid : 0);

            return new LaunchInfo(label + DescribeRoleSuffix(record), chain, hint.Evidence, summary);
        }

        // ---------- 2) 父进程 ----------
        if (record.ParentPid > 0 && byPid.TryGetValue(record.ParentPid, out var parent))
        {
            if (parent.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
            {
                return new LaunchInfo($"用户通过桌面/开始菜单/任务栏启动（explorer.exe，PID {parent.Pid}）",
                    chain, "父进程为资源管理器", summary);
            }

            // 跳过 extracted.exe / crashpad_handler 这类辅助进程，找到真正拉起的程序
            var effective = parent;
            var skipped = new List<string>();
            while (IsHelperProcess(effective.Name) &&
                   effective.ParentPid > 0 &&
                   byPid.TryGetValue(effective.ParentPid, out var grand) &&
                   skipped.Count < 3)
            {
                skipped.Add($"{effective.Name}({effective.Pid})");
                effective = grand;
            }

            var info = DescribeProcess(effective.Name, byPid);
            var text = FormatNode(StripExtension(effective.Name), true, info.Software, effective.Pid);

            if (skipped.Count > 0)
                text += $"（经由 {string.Join(" ← ", skipped)}）";

            if (effective.Name.Equals("explorer", StringComparison.OrdinalIgnoreCase))
                return new LaunchInfo($"用户通过桌面/开始菜单/任务栏启动（explorer.exe，PID {effective.Pid}）",
                    chain, "父进程为资源管理器", summary);

            if (effective.Name.Equals("services", StringComparison.OrdinalIgnoreCase))
                return new LaunchInfo($"由服务管理器 services.exe 启动（PID {effective.Pid}）",
                    chain, "父进程为 SCM", summary);

            if (effective.Pid <= 4)
                return new LaunchInfo($"由系统内核（PID {effective.Pid}）创建", chain, "父进程为内核", summary);

            // 辅助进程没能向上找到真正的宿主（例如微信的 extracted.exe），
            // 但同类进程还有一个主进程在跑：用同名主进程作为启动者。
            if (IsHelperProcess(effective.Name))
            {
                var root = FindRootByName(record, byPid);
                if (root is not null)
                {
                    var rootInfo = DescribeProcess(root.Name, byPid);
                    var rootText = FormatNode(StripExtension(root.Name), true, rootInfo.Software, root.Pid);
                    return new LaunchInfo(rootText + DescribeRoleSuffix(record), chain, "同名主进程", summary);
                }
            }

            var friendly = effective.Name.ToLowerInvariant() switch
            {
                "svchost" => $"由服务宿主 svchost.exe 启动（PID {effective.Pid}）",
                "winlogon" or "userinit" => $"登录时由系统启动（{text}）",
                _ => text,
            };

            return new LaunchInfo(friendly, chain, skipped.Count > 0 ? "父进程链" : "父进程 PID", summary);
        }

        if (record.ParentPid > 0)
            return new LaunchInfo($"父进程 PID {record.ParentPid}（已退出，无法确定启动程序）", chain,
                "父进程已不在", summary);

        return new LaunchInfo("未知（没有命令行也没有父进程信息）", chain, "", summary);
    }

    /// <summary>把进程链拼成 "A(pid) ← B(pid) ← C(pid)"。</summary>
    public static string BuildChain(ProcessRecord record, IReadOnlyDictionary<int, ProcessRecord> byPid)
    {
        var builder = new StringBuilder();
        builder.Append($"{record.Name}({record.Pid})");

        var current = record;
        var guard = 0;
        while (guard++ < 5 && current.ParentPid > 0 && byPid.TryGetValue(current.ParentPid, out var parent))
        {
            builder.Append(" ← ").Append($"{parent.Name}({parent.Pid})");
            current = parent;
        }

        if (current.ParentPid > 0 && !byPid.ContainsKey(current.ParentPid))
            builder.Append($" ← 父进程 PID {current.ParentPid}（已退出）");

        return builder.ToString();
    }

    private sealed record HostHint(string HostExeName, string Evidence, bool IsExeName);

    /// <summary>
    /// 从自身及祖先的命令行里找宿主程序线索：
    /// --webview-exe-name=clash-verge.exe（WebView2 宿主）、--user-data-dir=…（Electron/Chromium）。
    /// </summary>
    private static HostHint? FindHostHint(ProcessRecord record, IReadOnlyDictionary<int, ProcessRecord> byPid, int depth)
    {
        var current = record;
        for (var level = 0; level <= depth && current is not null; level++)
        {
            var (named, _) = ParseArguments(current.CommandLine);
            if (named.TryGetValue("webview-exe-name", out var webviewHost) && !string.IsNullOrWhiteSpace(webviewHost))
            {
                var host = webviewHost.Trim();
                if (!IsHelperProcess(StripExtension(host)))
                {
                    var evidence = level == 0
                        ? $"命令行 --webview-exe-name={host}（内嵌 WebView2，由该程序拉起）"
                        : $"父进程 {current.Name} 的命令行 --webview-exe-name={host}（本进程是其子进程）";
                    return new HostHint(host, AppendDataDir(evidence, named), true);
                }
            }

            if (named.TryGetValue("user-data-dir", out var dataDir) && !string.IsNullOrWhiteSpace(dataDir))
            {
                var token = ExtractAppToken(dataDir);
                if (!string.IsNullOrWhiteSpace(token) && !IsHelperProcess(token) && !GenericTokens.Contains(token))
                {
                    var evidence = $"命令行 --user-data-dir={Truncate(dataDir, 120)}（数据目录标识宿主程序 {token}）";
                    return new HostHint(token, evidence, false);
                }
            }

            if (named.TryGetValue("app-path", out var appPath) && !string.IsNullOrWhiteSpace(appPath))
            {
                var fileName = SafeFileName(appPath);
                // 只认真正的 exe；Electron 的 --app-path 常指向 app.asar 之类，不能当程序名
                if (!string.IsNullOrWhiteSpace(fileName) &&
                    fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                    !IsHelperProcess(StripExtension(fileName)))
                    return new HostHint(fileName, $"命令行 --app-path={Truncate(appPath, 120)}", true);
            }

            if (current.ParentPid <= 0 || !byPid.TryGetValue(current.ParentPid, out var parent))
                break;
            current = parent;
        }

        return null;
    }

    private static string AppendDataDir(string evidence, Dictionary<string, string> named)
        => named.TryGetValue("user-data-dir", out var dataDir) && !string.IsNullOrWhiteSpace(dataDir)
            ? evidence + $"；数据目录 {Truncate(dataDir, 100)}"
            : evidence;

    /// <summary>把 --user-data-dir 的路径转成应用标识：…\io.github.clash-verge-rev.clash-verge → clash-verge。</summary>
    private static string ExtractAppToken(string dataDir)
    {
        var path = dataDir.Trim().Trim('"').TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(path))
            return "";

        string? segment = null;
        try
        {
            foreach (var part in path.Split('\\', '/').Reverse())
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                if (DataDirNoise.Contains(part))
                    continue;

                segment = part;
                break;
            }
        }
        catch
        {
            return "";
        }

        if (string.IsNullOrWhiteSpace(segment))
            return "";

        // 去掉反向域名前缀与点号包名：io.github.xxx / com.tencent.xxx
        var dot = segment.LastIndexOf('.');
        if (dot > 0 && dot < segment.Length - 1)
            segment = segment[(dot + 1)..];

        segment = Regex.Replace(segment, @"[-_.]?v?\d+(\.\d+)+.*$", "");
        return segment;
    }

    private static bool IsHelperProcess(string name)
    {
        var value = StripExtension((name ?? "").Trim());
        if (value.Length == 0)
            return false;

        if (value.ToLowerInvariant() is "extracted" or "crashpad_handler" or "conhost" or "werfault" or "werfaultsecure" or "taskhostw")
            return true;

        // 纯版本号命名的临时程序（如微信更新时的 4.1.15.56.exe）也算辅助进程
        return Regex.IsMatch(value, @"^\d+(\.\d+)+$");
    }

    private static string StripExtension(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>格式化成 "clash-verge.exe（Clash Verge，PID 1234）"；软件名与进程名相同则只显示一次。</summary>
    private static string FormatNode(string name, bool hasRealExe, string software, int pid)
    {
        var exe = hasRealExe && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;

        var distinct = !string.IsNullOrWhiteSpace(software) &&
                       !software.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                       software.Length > 2;

        if (pid > 0)
            return distinct ? $"{exe}（{software}，PID {pid}）" : $"{exe}（PID {pid}）";

        return distinct ? $"{exe}（{software}）" : exe;
    }

    private sealed record ProcessDescription(string Software, string ExeName, int Pid);

    /// <summary>
    /// 把 exe 名（或数据目录标识）换成软件名 + 真实进程：
    /// 优先知识库 → 同名进程 → 名字相近的进程（数据目录标识常与 exe 略有差异）→ 首字母大写。
    /// </summary>
    private static ProcessDescription DescribeProcess(string tokenOrExe, IReadOnlyDictionary<int, ProcessRecord> byPid)
    {
        var name = (tokenOrExe ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        var knowledge = KnowledgeBase.Find(name);
        if (knowledge is not null)
            return new ProcessDescription(knowledge.Software, name, 0);

        foreach (var candidate in byPid.Values)
        {
            if (!candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            return new ProcessDescription(PreferredName(candidate), candidate.Name, candidate.Pid);
        }

        // 数据目录标识（clash-verge-rev）与真实 exe（clash-verge）不完全一致时，取相近的那个
        if (name.Length >= 4)
        {
            foreach (var candidate in byPid.Values)
            {
                if (candidate.Name.Length < 4)
                    continue;

                if (!candidate.Name.Contains(name, StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains(candidate.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                return new ProcessDescription(PreferredName(candidate), candidate.Name, candidate.Pid);
            }
        }

        return new ProcessDescription(Prettify(name), name, 0);
    }

    private static string PreferredName(ProcessRecord candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.ProductName) && candidate.ProductName.Length > 2)
            return candidate.ProductName;
        if (!string.IsNullOrWhiteSpace(candidate.Company))
            return candidate.Company;
        return Prettify(candidate.Name);
    }

    private static string Prettify(string token)
    {
        if (Aliases.TryGetValue(token, out var alias))
            return alias;

        var parts = token.Replace('_', '-').Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return token;

        return string.Join(' ', parts.Select(part => part.Length <= 2
            ? part.ToUpperInvariant()
            : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string DescribeRoleSuffix(ProcessRecord record)
    {
        var (named, _) = ParseArguments(record.CommandLine);
        if (!named.TryGetValue("type", out var type) || string.IsNullOrWhiteSpace(type))
            return "";

        var known = type.ToLowerInvariant() switch
        {
            "renderer" => "渲染进程",
            "gpu-process" => "GPU 进程",
            "utility" => "工具进程",
            "crashpad-handler" => "崩溃报告进程",
            "zygote" => "孵化进程",
            _ => "",
        };

        return known.Length > 0 ? $" · 作为{known}" : $" · 子进程（{type}）";
    }

    /// <summary>找同名进程中最早启动的那个（通常是主进程），用于辅助子进程归属。</summary>
    private static ProcessRecord? FindRootByName(ProcessRecord record, IReadOnlyDictionary<int, ProcessRecord> byPid)
    {
        ProcessRecord? best = null;
        foreach (var candidate in byPid.Values)
        {
            if (candidate.Pid == record.Pid ||
                !candidate.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (record.StartTime is null || candidate.StartTime is null)
            {
                best ??= candidate;
                continue;
            }

            if (candidate.StartTime < record.StartTime &&
                (best?.StartTime is null || candidate.StartTime < best.StartTime))
                best = candidate;
        }

        return best;
    }

    /// <summary>解析命令行：返回 --key=value 字典与位置参数列表。</summary>
    public static (Dictionary<string, string> Named, List<string> Positional) ParseArguments(string commandLine)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var positional = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
            return (named, positional);

        foreach (var token in Tokenize(commandLine))
        {
            if (token.StartsWith("--", StringComparison.Ordinal))
            {
                var separator = token.IndexOf('=');
                if (separator > 2)
                    named[token[2..separator]] = token[(separator + 1)..].Trim('"');
                else
                    named[token[2..]] = "";
            }
            else
            {
                positional.Add(token);
            }
        }

        return (named, positional);
    }

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (builder.Length > 0)
                {
                    tokens.Add(builder.ToString());
                    builder.Clear();
                }

                continue;
            }

            builder.Append(c);
        }

        if (builder.Length > 0)
            tokens.Add(builder.ToString());

        return tokens;
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path.Trim().Trim('"'));
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "" : text.Length <= max ? text : text[..max] + "…";
}
