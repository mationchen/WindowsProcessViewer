using System.Diagnostics;
using WindowsProcessViewer.Models;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 采集全部进程：WMI 提供命令行/父进程/路径，System.Diagnostics 提供内存/线程/CPU，
/// Win32 API 提供运行账户与提权状态，WMI 服务表提供 PID→服务映射。
/// 单个进程读不到信息不会中断整体采集。
/// </summary>
public sealed class ProcessCollector
{
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _previousCpu = new();
    private readonly Dictionary<string, VersionMeta> _versionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _selfElevated = NativeProcess.IsSelfElevated();
    private readonly AppCache? _cache;

    public ProcessCollector(AppCache? cache = null)
    {
        _cache = cache;
    }

    public bool SelfElevated => _selfElevated;

    public List<ProcessRecord> Collect()
    {
        var start = DateTime.UtcNow;
        var rawProcesses = WmiQuery.QueryProcesses();
        var services = WmiQuery.QueryServices();
        var autostart = AutostartScanner.Scan();

        var list = new List<ProcessRecord>(512);
        var nameByPid = new Dictionary<int, string>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var record = BuildRecord(process, rawProcesses, services, autostart);
                    if (record is null)
                        continue;

                    list.Add(record);
                    nameByPid[record.Pid] = record.Name;
                }
                catch (Exception ex)
                {
                    Log.Warn($"采集进程 {SafeId(process)} 失败：{ex.Message}");
                }
            }
        }

        // 第二遍补父进程名：第一遍时父进程可能还没采集到
        foreach (var record in list)
        {
            if (nameByPid.TryGetValue(record.ParentPid, out var parentName))
                record.ParentName = parentName;
        }

        PruneCpuCache(nameByPid);

        Log.Info($"进程采集完成：{list.Count} 个，耗时 {(DateTime.UtcNow - start).TotalMilliseconds:F0} ms，" +
                 $"WMI {rawProcesses.Count} 条，服务 {services.Count} 条，自启项 {autostart.Count} 条");
        return list;
    }

    private ProcessRecord? BuildRecord(
        Process process,
        Dictionary<int, RawProcessInfo> rawProcesses,
        Dictionary<int, ServiceInfo> services,
        Dictionary<string, string> autostart)
    {
        int pid;
        try
        {
            pid = process.Id;
        }
        catch (InvalidOperationException)
        {
            return null; // 进程在采集过程中已退出
        }

        rawProcesses.TryGetValue(pid, out var raw);

        var record = new ProcessRecord
        {
            Pid = pid,
            Name = ResolveName(process, raw, pid),
            IsCurrentProcess = pid == Environment.ProcessId,
            Path = raw?.Path ?? TryGetMainModule(process),
            CommandLine = raw?.CommandLine ?? "",
            ParentPid = raw?.ParentPid ?? 0,
            SessionId = raw?.SessionId ?? TryGetSessionId(process),
            StartTime = raw?.Created ?? TryGetStartTime(process),
        };

        TryFillMemory(record, process);

        record.CacheKey = AppCache.BuildKey(record.Path, record.Name);
        var meta = GetVersionMeta(record.Path, record.CacheKey, record.Name);
        record.Company = meta.Company;
        record.ProductName = meta.Product;
        record.FileDescription = meta.Description;
        record.FileVersion = meta.FileVersion;
        record.SignatureStatus = meta.Signature;

        var (owner, elevated) = NativeProcess.Query(pid);
        record.Owner = owner;
        record.IsElevated = elevated;

        if (services.TryGetValue(pid, out var service))
        {
            record.IsService = true;
            record.ServiceName = service.Name;
            record.ServiceDisplayName = service.DisplayName;
            record.ServiceAutoStart = string.Equals(service.StartMode, "Auto", StringComparison.OrdinalIgnoreCase);
        }

        var exeName = SafeFileName(record.Path);
        if (!string.IsNullOrEmpty(exeName) && autostart.TryGetValue(exeName, out var autostartSource))
            record.Autostart = autostartSource;
        else if (autostart.TryGetValue(record.Name + ".exe", out var byName))
            record.Autostart = byName;

        record.CpuPercent = CalculateCpu(process, pid);
        return record;
    }

    private static string ResolveName(Process process, RawProcessInfo? raw, int pid)
    {
        if (pid == 0)
            return "System Idle Process";
        if (pid == 4)
            return "System";

        try
        {
            var name = process.ProcessName;
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取 PID {pid} 进程名失败：{ex.Message}");
        }

        var fromPath = SafeFileName(raw?.Path);
        return string.IsNullOrWhiteSpace(fromPath) ? $"PID {pid}" : Path.GetFileNameWithoutExtension(fromPath);
    }

    private static string TryGetMainModule(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? "";
        }
        catch
        {
            // 跨位数或权限不足时读不到，属于正常情况
            return "";
        }
    }

    private static int TryGetSessionId(Process process)
    {
        try
        {
            return process.SessionId;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTime? TryGetStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }

    private static void TryFillMemory(ProcessRecord record, Process process)
    {
        try
        {
            record.WorkingSetMb = process.WorkingSet64 / 1024d / 1024d;
        }
        catch
        {
            // 受保护进程读不到内存，保持 0
        }

        try
        {
            record.PrivateMb = process.PrivateMemorySize64 / 1024d / 1024d;
        }
        catch
        {
        }

        try
        {
            record.ThreadCount = process.Threads.Count;
        }
        catch
        {
        }
    }

    /// <summary>
    /// CPU 占用 = 两次刷新之间的处理器时间差 / 墙钟时间 / 核心数。
    /// 首次刷新没有上一次采样，显示 0。
    /// </summary>
    private double CalculateCpu(Process process, int pid)
    {
        try
        {
            var cpuTime = process.TotalProcessorTime;
            var now = DateTime.UtcNow;
            var percent = 0d;

            if (_previousCpu.TryGetValue(pid, out var previous))
            {
                var wallSeconds = (now - previous.At).TotalSeconds;
                var cpuSeconds = (cpuTime - previous.Cpu).TotalSeconds;
                if (wallSeconds > 0.05 && cpuSeconds >= 0)
                    percent = Math.Clamp(cpuSeconds / wallSeconds / Environment.ProcessorCount * 100d, 0d, 100d);
            }

            _previousCpu[pid] = (cpuTime, now);
            return percent;
        }
        catch
        {
            return 0;
        }
    }

    private void PruneCpuCache(Dictionary<int, string> alive)
    {
        if (_previousCpu.Count <= alive.Count)
            return;

        var dead = _previousCpu.Keys.Where(pid => !alive.ContainsKey(pid)).ToList();
        foreach (var pid in dead)
            _previousCpu.Remove(pid);
    }

    private VersionMeta GetVersionMeta(string path, string cacheKey, string processName)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new VersionMeta("", "", "", "", "路径不可读");

        if (_versionCache.TryGetValue(path, out var cached))
            return cached;

        var (size, ticks) = ReadFileStamp(path);

        // 文件没变就直接用 SQLite 里的结论，不必再读版本信息、跑一遍签名校验
        if (_cache is not null)
        {
            var app = _cache.Find(cacheKey);
            if (app is not null &&
                app.FileSize == size &&
                app.WriteTicks == ticks &&
                !string.IsNullOrWhiteSpace(app.Signature))
            {
                var fromCache = new VersionMeta(app.Company, app.Product, app.Description, app.FileVersion, app.Signature);
                _versionCache[path] = fromCache;
                return fromCache;
            }
        }

        var company = "";
        var product = "";
        var description = "";
        var version = "";
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            company = info.CompanyName ?? "";
            product = info.ProductName ?? "";
            description = info.FileDescription ?? "";
            version = info.FileVersion ?? "";
        }
        catch (Exception ex)
        {
            Log.Warn($"读取文件版本信息失败 {path}：{ex.Message}");
        }

        var meta = new VersionMeta(company, product, description, version,
            SignatureChecker.Describe(path, company));
        _versionCache[path] = meta;

        // 把文件指纹与版本信息写进缓存，下次启动直接命中
        if (_cache is not null)
        {
            var app = _cache.GetOrCreate(cacheKey, processName, path);
            app.FileSize = size;
            app.WriteTicks = ticks;
            app.Company = company;
            app.Product = product;
            app.Description = description;
            app.FileVersion = version;
            app.Signature = meta.Signature;
            _cache.MarkDirty(app);
        }

        return meta;
    }

    private static (long Size, long Ticks) ReadFileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (0, 0);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string SafeFileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return "";
        }
    }

    private static string SafeId(Process process)
    {
        try
        {
            return process.Id.ToString();
        }
        catch
        {
            return "?";
        }
    }

    private readonly record struct VersionMeta(
        string Company,
        string Product,
        string Description,
        string FileVersion,
        string Signature);
}
