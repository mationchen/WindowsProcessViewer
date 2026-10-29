using System.Management;

namespace WindowsProcessViewer.Services;

public sealed record RawProcessInfo(
    int Pid,
    int ParentPid,
    string? Path,
    string? CommandLine,
    int SessionId,
    DateTime? Created);

public sealed record ServiceInfo(
    string Name,
    string DisplayName,
    string State,
    string StartMode);

/// <summary>
/// 一次 WMI 查询拿到全部进程的命令行/父进程/可执行路径，以及 PID→服务映射。
/// WMI 不可用时返回空表，采集器会退回 System.Diagnostics.Process 的有限信息。
/// </summary>
public static class WmiQuery
{
    public static Dictionary<int, RawProcessInfo> QueryProcesses()
    {
        var map = new Dictionary<int, RawProcessInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, ExecutablePath, CommandLine, SessionId, CreationDate " +
                "FROM Win32_Process");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    try
                    {
                        var pid = Convert.ToInt32(mo["ProcessId"] ?? -1);
                        if (pid < 0)
                            continue;

                        var parent = Convert.ToInt32(mo["ParentProcessId"] ?? 0);
                        var session = Convert.ToInt32(mo["SessionId"] ?? 0);
                        DateTime? created = null;
                        if (mo["CreationDate"] is string dmtf && !string.IsNullOrWhiteSpace(dmtf))
                        {
                            try
                            {
                                created = ManagementDateTimeConverter.ToDateTime(dmtf);
                            }
                            catch
                            {
                                created = null;
                            }
                        }

                        map[pid] = new RawProcessInfo(
                            pid,
                            parent,
                            mo["ExecutablePath"] as string,
                            mo["CommandLine"] as string,
                            session,
                            created);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"解析 WMI 进程记录失败：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("WMI 查询 Win32_Process 失败，本次仅显示基础信息", ex);
        }

        return map;
    }

    public static Dictionary<int, ServiceInfo> QueryServices()
    {
        var map = new Dictionary<int, ServiceInfo>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, DisplayName, State, StartMode FROM Win32_Service");
            using var collection = searcher.Get();
            foreach (ManagementObject mo in collection)
            {
                using (mo)
                {
                    try
                    {
                        var pid = Convert.ToInt32(mo["ProcessId"] ?? 0);
                        if (pid <= 0)
                            continue;

                        var info = new ServiceInfo(
                            (mo["Name"] as string) ?? "",
                            (mo["DisplayName"] as string) ?? "",
                            (mo["State"] as string) ?? "",
                            (mo["StartMode"] as string) ?? "");

                        // 一个 svchost 可能承载多个服务，保留第一个便于定位；完整信息可在 services.msc 查看。
                        map.TryAdd(pid, info);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"解析 WMI 服务记录失败：{ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("WMI 查询 Win32_Service 失败，服务归属将不可用", ex);
        }

        return map;
    }
}
