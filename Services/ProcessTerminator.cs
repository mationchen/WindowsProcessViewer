using System.Diagnostics;
using WindowsProcessViewer.Models;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 结束进程的统一入口：保护判断集中在这里，界面（右键菜单、确认框）只负责问用户。
/// 保护规则：系统核心进程、PID ≤ 4、本程序自身，一律拒绝。
/// </summary>
public static class ProcessTerminator
{
    public static bool IsBlocked(ProcessRecord record, int selfPid, out string reason)
    {
        if (record.Pid <= 4)
        {
            reason = "PID ≤ 4 属于系统内核/空闲进程，无法结束。";
            return true;
        }

        if (record.Pid == selfPid)
        {
            reason = "不能结束本工具自身。";
            return true;
        }

        if (KnowledgeBase.IsForbidden(record.Name))
        {
            reason = $"“{record.Name}”是系统核心进程，结束它会导致蓝屏或立即重启。";
            return true;
        }

        reason = "";
        return false;
    }

    /// <summary>结束进程；失败时抛出原始异常（通常是 Win32Exception 拒绝访问），由界面转成中文提示。</summary>
    public static void Kill(ProcessRecord record)
    {
        using var process = Process.GetProcessById(record.Pid);
        process.Kill();
    }
}
