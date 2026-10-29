using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 通过 Win32 API 读取进程的运行账户与是否提权。
/// 用 P/Invoke 而不是逐进程 WMI 调用，几百个进程也只需几十毫秒。
/// </summary>
internal static class NativeProcess
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenUser = 1;
    private const int TokenElevation = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        IntPtr tokenInformation, int tokenInformationLength, out int returnLength);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupAccountSid(string? lpSystemName, IntPtr sid, StringBuilder lpName,
        ref int cchName, StringBuilder lpReferencedDomainName, ref int cchReferencedDomainName,
        out int peUse);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 返回进程运行账户（DOMAIN\user）和是否以管理员权限运行。
    /// 受保护进程可能两者都读不到，此时返回空字符串/false，调用方按“权限不足”处理。
    /// </summary>
    public static (string Owner, bool Elevated) Query(int pid)
    {
        var owner = "";
        var elevated = false;
        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
            return (owner, elevated);

        try
        {
            if (!OpenProcessToken(hProcess, TOKEN_QUERY, out var hToken))
                return (owner, elevated);

            try
            {
                owner = ReadOwner(hToken);
                elevated = ReadElevation(hToken);
            }
            finally
            {
                CloseHandle(hToken);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"读取进程 {pid} 令牌失败：{ex.Message}");
        }
        finally
        {
            CloseHandle(hProcess);
        }

        return (owner, elevated);
    }

    private static string ReadOwner(IntPtr hToken)
    {
        GetTokenInformation(hToken, TokenUser, IntPtr.Zero, 0, out var needed);
        if (needed <= 0)
            return "";

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(hToken, TokenUser, buffer, needed, out _))
                return "";

            // TOKEN_USER 的第一个字段是 SID_AND_ATTRIBUTES，其第一个字段是 PSID
            var sid = Marshal.ReadIntPtr(buffer);
            if (sid == IntPtr.Zero)
                return "";

            var name = new StringBuilder(256);
            var domain = new StringBuilder(256);
            var nameLen = name.Capacity;
            var domainLen = domain.Capacity;
            if (!LookupAccountSid(null, sid, name, ref nameLen, domain, ref domainLen, out _))
                return "";

            var domainText = domain.ToString();
            return string.IsNullOrEmpty(domainText) ? name.ToString() : $"{domainText}\\{name}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ReadElevation(IntPtr hToken)
    {
        var size = sizeof(int);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            // TOKEN_ELEVATION { DWORD TokenIsElevated; }
            if (!GetTokenInformation(hToken, TokenElevation, buffer, size, out _))
                return false;
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>判断当前程序是否以管理员身份运行。</summary>
    public static bool IsSelfElevated()
    {
        try
        {
            var (_, elevated) = Query(Environment.ProcessId);
            return elevated;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 Win32Exception 转成中文提示（结束进程失败时用）。</summary>
    public static string DescribeError(Exception ex)
    {
        if (ex is Win32Exception w32)
        {
            return w32.NativeErrorCode switch
            {
                5 => "拒绝访问：该进程权限比当前程序高（多为系统进程或以管理员运行的程序），请以管理员身份运行本工具后再试。",
                87 => "参数错误：进程可能已经退出。",
                _ => $"系统错误 {w32.NativeErrorCode}：{w32.Message}",
            };
        }

        return ex.Message;
    }
}
