using System.Text;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 简单日志：按天写入 exe 同目录 Logs\WindowsProcessViewer_yyyyMMdd.log。
/// 只记录异常与关键事件，不记录 API Key。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string LogDirectory => Path.Combine(AppContext.BaseDirectory, "Logs");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"WindowsProcessViewer_{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程；此处不做任何提示，避免递归报错。
        }
    }
}
