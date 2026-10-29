using Microsoft.Win32;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 扫描开机自启项（HKCU/HKLM 的 Run 键 + 启动文件夹），用于回答“谁启动的”。
/// 只做文件名匹配，不做深度计划任务/驱动分析。
/// </summary>
public static class AutostartScanner
{
    public static Dictionary<string, string> Scan()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        ReadRunKey(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "当前用户注册表 Run", map);
        ReadRunKey(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "系统注册表 Run", map);
        ReadRunKey(Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "系统注册表 Run (32 位)", map);

        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "当前用户启动文件夹", map);
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "公共启动文件夹", map);

        return map;
    }

    private static void ReadRunKey(RegistryKey root, string subKey, string source, Dictionary<string, string> map)
    {
        try
        {
            using var key = root.OpenSubKey(subKey);
            if (key is null)
                return;

            foreach (var valueName in key.GetValueNames())
            {
                var command = key.GetValue(valueName) as string;
                var exeName = ExtractExecutableName(command);
                if (!string.IsNullOrEmpty(exeName))
                    map.TryAdd(exeName, $"{source}：{valueName}");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取自启项 {subKey} 失败：{ex.Message}");
        }
    }

    private static void ReadStartupFolder(string folder, string source, Dictionary<string, string> map)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!string.IsNullOrEmpty(name))
                    map.TryAdd(name, source);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取启动文件夹 {folder} 失败：{ex.Message}");
        }
    }

    /// <summary>从注册表命令行里取可执行文件名：支持带引号、带参数、explorer 启动 lnk 等写法。</summary>
    private static string? ExtractExecutableName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        var text = commandLine.Trim().Trim('"');
        if (text.StartsWith('"'))
            text = text[1..];

        var quoteEnd = text.IndexOf('"');
        if (quoteEnd > 0)
            text = text[..quoteEnd];
        else
        {
            var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex > 0)
                text = text[..(exeIndex + 4)];
        }

        text = text.Trim();
        var fileName = Path.GetFileName(text);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        return fileName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) ? null : fileName;
    }
}
