using System.Text;
using WindowsProcessViewer.Forms;
using WindowsProcessViewer.Services;

namespace WindowsProcessViewer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 无界面导出：WindowsProcessViewer.exe --export out.csv
        // 便于批量留档或在没有显示器的环境下验证采集逻辑。
        if (TryGetArgument(args, "--export", out var exportPath))
            return RunExport(exportPath);

        // AI 自检：--ai-test 验证 Key/区域/模型，并跑一次真实分析，结果输出到控制台
        if (args.Any(a => a.Equals("--ai-test", StringComparison.OrdinalIgnoreCase)))
            return RunAiSelfTest();

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) => ReportFatal(e.Exception, "界面线程异常");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportFatal(e.ExceptionObject as Exception, "未处理异常");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("未观察到的后台任务异常", e.Exception);
            e.SetObserved();
        };

        var settings = ConfigService.Load(out var error);
        Log.Info($"程序启动，版本 {typeof(Program).Assembly.GetName().Version}，" +
                 $"提权={NativeProcess.IsSelfElevated()}，AI 配置={(string.IsNullOrWhiteSpace(ConfigService.ResolveApiKey(settings)) ? "未配置" : "已配置")}");

        var startArea = ResolveStartArea(args, settings);
        using var cache = new AppCache();
        Application.Run(new MainForm(settings, error, startArea, cache));
        return 0;
    }

    /// <summary>
    /// 主窗口放到哪块屏：--screen N 优先，其次 appsettings.json 的 Ui.StartScreen，
    /// 都没有则用系统默认位置。N 为 Screen.AllScreens 顺序（1 开始）。
    /// </summary>
    private static Rectangle? ResolveStartArea(string[] args, Models.AppSettings settings)
    {
        var index = 0;
        if (TryGetIntArgument(args, "--screen", out var fromArgs))
            index = fromArgs;
        else if (settings.Ui.StartScreen > 0)
            index = settings.Ui.StartScreen;

        if (index <= 0)
            return null;

        var screens = Screen.AllScreens;
        if (index <= screens.Length)
        {
            Log.Info($"启动屏幕：{screens[index - 1].DeviceName} {screens[index - 1].WorkingArea}");
            return screens[index - 1].WorkingArea;
        }

        Log.Warn($"指定的启动屏幕 {index} 超出范围（当前 {screens.Length} 个显示器），改用默认位置");
        return null;
    }

    private static bool TryGetIntArgument(string[] args, string name, out int value)
    {
        value = 0;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var parsed))
            {
                value = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryGetArgument(string[] args, string name, out string value)
    {
        value = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[i + 1]
                : $"进程列表_{DateTime.Now:yyyyMMdd_HHmmss}.csv";

            value = Path.GetFullPath(candidate);
            return true;
        }

        return false;
    }

    private static int RunExport(string path)
    {
        try
        {
            using var cache = new AppCache();
            var collector = new ProcessCollector(cache);
            var records = collector.Collect();
            new ProcessAnalyzer(collector.SelfElevated, cache).AnalyzeAll(records);
            cache.Flush();
            CsvExporter.Export(path, records.OrderBy(r => r.Pid));

            Log.Info($"命令行导出完成：{records.Count} 行 → {path}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error($"命令行导出失败：{path}", ex);
            return 1;
        }
    }

    /// <summary>
    /// --ai-test：验证 Key / 区域 / 模型是否可用，并跑一次真实分析（取内存占用前 3 的进程），
    /// 用来确认"能连通"和"返回的 JSON 能解析"两件事。输出到控制台，退出码 0 表示成功。
    /// </summary>
    private static int RunAiSelfTest()
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 无控制台时忽略（WinExe 由父进程重定向时通常仍可用）
        }

        var settings = ConfigService.Load(out var configError);
        Console.WriteLine($"配置文件：{ConfigService.ConfigPath}");
        if (!string.IsNullOrWhiteSpace(configError))
            Console.WriteLine($"配置提示：{configError}");

        var apiKey = ConfigService.ResolveApiKey(settings);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Console.WriteLine("未配置 MiniMax.ApiKey（也可用环境变量 MINIMAX_API_KEY）。");
            return 2;
        }

        Console.WriteLine($"接口：{settings.MiniMax.Endpoint}");
        Console.WriteLine($"模型：{settings.MiniMax.Model}");

        var ai = new AiService();
        using var cache = new AppCache();
        try
        {
            var ping = ai.PingAsync(settings, apiKey, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"连通性：正常（{ping}）");

            var collector = new ProcessCollector(cache);
            var records = collector.Collect()
                .OrderByDescending(r => r.WorkingSetMb)
                .Take(3)
                .ToList();
            new ProcessAnalyzer(collector.SelfElevated, cache).AnalyzeAll(records);
            Console.WriteLine($"取样进程：{string.Join("、", records.Select(r => $"{r.Name}({r.Pid})"))}");

            var result = ai.AnalyzeAsync(records, settings, apiKey, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"解析结果：{result.Verdicts.Count}/{records.Count} 条，" +
                              $"token {result.PromptTokens}+{result.CompletionTokens}");

            foreach (var verdict in result.Verdicts)
            {
                Console.WriteLine($"  pid={verdict.Pid} 软件={verdict.Software} 作用={verdict.Purpose} " +
                                  $"能否关闭={verdict.CanClose} 风险={verdict.Risk}");

                var record = records.FirstOrDefault(r => r.Pid == verdict.Pid);
                if (record is null)
                    continue;

                record.AiSoftware = verdict.Software;
                record.AiPurpose = verdict.Purpose;
                record.AiLaunchedBy = verdict.LaunchedBy;
                record.AiCanClose = verdict.CanClose;
                record.AiCloseMethod = verdict.CloseMethod;
                record.AiRisk = verdict.Risk;
                record.AiSummary = verdict.Note;
                record.AiDetail = $"所属软件：{verdict.Software}\n作用说明：{verdict.Purpose}\n" +
                                  $"谁启动的：{verdict.LaunchedBy}\n能否关闭：{verdict.CanClose}\n" +
                                  $"如何关闭：{verdict.CloseMethod}\n风险等级：{verdict.Risk}";
                cache.UpdateFromRecord(record, result.Model);
            }

            cache.Flush();
            Console.WriteLine($"已写入 SQLite 缓存（{cache.Count} 个应用）：{AppCache.DatabasePath}");

            if (result.Verdicts.Count == 0)
            {
                var raw = result.RawText.Length > 800 ? result.RawText[..800] : result.RawText;
                Console.WriteLine("未能解析为 JSON 数组，原始回复：");
                Console.WriteLine(raw);
                return 1;
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"失败：{ex.Message}");
            Log.Error("--ai-test 失败", ex);
            return 1;
        }
    }

    private static void ReportFatal(Exception? ex, string title)
    {
        Log.Error(title, ex);
        try
        {
            MessageBox.Show(
                $"{title}：{ex?.Message}\n\n详细信息已写入日志：\n{Log.LogDirectory}\n\n程序将退出。",
                "Windows 进程查看器",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // 连弹窗都失败时不再处理，避免递归
        }
    }
}
