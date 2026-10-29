using System.Text;
using WindowsProcessViewer.Models;

namespace WindowsProcessViewer.Services;

/// <summary>导出 CSV（UTF-8 BOM，Excel 直接双击不乱码）。</summary>
public static class CsvExporter
{
    private static readonly string[] Headers =
    [
        "进程名", "PID", "所属软件", "作用说明", "谁启动的", "启动链", "启动依据", "能否关闭", "如何关闭",
        "风险", "类别", "运行账户", "内存(MB)", "CPU(%)", "数字签名", "父进程PID",
        "启动时间", "文件路径", "命令行", "AI结论",
    ];

    public static void Export(string path, IEnumerable<ProcessRecord> records)
    {
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", Headers.Select(Escape)));

        foreach (var r in records)
        {
            var row = new[]
            {
                r.Name,
                r.Pid.ToString(),
                r.Software,
                r.Purpose,
                r.LaunchedBy,
                r.LaunchChain,
                r.LaunchEvidence,
                r.CanClose,
                r.CloseMethod,
                r.Risk,
                r.Category,
                r.Owner,
                r.WorkingSetMb.ToString("F1"),
                r.CpuPercent.ToString("F1"),
                r.SignatureStatus,
                r.ParentPid.ToString(),
                r.StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                r.Path,
                r.CommandLine,
                r.AiSummary,
            };

            builder.AppendLine(string.Join(",", row.Select(Escape)));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
        Log.Info($"已导出 {path}");
    }

    private static string Escape(string? value)
    {
        var text = value ?? "";
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        return text;
    }
}
