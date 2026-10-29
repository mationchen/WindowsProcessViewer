using System.Runtime.InteropServices;

namespace WindowsProcessViewer.Services;

/// <summary>
/// 数字签名检测：调用 WinVerifyTrust，同时覆盖“嵌入签名”和“目录签名（catalog）”两种情况，
/// 因此 Windows 系统文件也能正确识别为已签名。
/// 只做本地校验，不联网吊销检查。
/// </summary>
internal static class SignatureChecker
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdSaferFlag = 0x00000100;

    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustESubjectFormUnknown = unchecked((int)0x800B0003);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEExpired = unchecked((int)0x800B0101);
    private const int TrustEBadDigest = unchecked((int)0x80096010);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid actionId, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    /// <summary>返回用于界面展示的中文签名结论。</summary>
    public static string Describe(string path, string company)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return "无法校验（文件不可读）";

        var result = Verify(path);
        var microsoft = company.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);

        return result switch
        {
            0 => microsoft
                ? "Microsoft 签名（已验证）"
                : string.IsNullOrWhiteSpace(company) ? "已签名（已验证）" : $"已签名（已验证）：{company}",
            TrustENoSignature or TrustESubjectFormUnknown or TrustEProviderUnknown => "无数字签名",
            TrustEBadDigest => "签名无效：文件内容与签名不符（可能被篡改）",
            CertEUntrustedRoot => "签名不受信任：根证书不在受信任列表",
            CertEExpired => "签名已过期",
            _ => $"签名校验未通过（0x{result:X8}）",
        };
    }

    private static int Verify(string path)
    {
        var fileInfoSize = Marshal.SizeOf<WintrustFileInfo>();
        var dataSize = Marshal.SizeOf<WintrustData>();

        var fileInfoPointer = Marshal.AllocHGlobal(fileInfoSize);
        var dataPointer = Marshal.AllocHGlobal(dataSize);
        var pathPointer = Marshal.StringToHGlobalUni(path);
        var action = GenericVerifyV2;

        try
        {
            var fileInfo = new WintrustFileInfo
            {
                Size = (uint)fileInfoSize,
                FilePath = pathPointer,
                FileHandle = IntPtr.Zero,
                KnownSubject = IntPtr.Zero,
            };
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

            var data = new WintrustData
            {
                Size = (uint)dataSize,
                PolicyCallbackData = IntPtr.Zero,
                SipClientData = IntPtr.Zero,
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = WtdStateActionVerify,
                StateData = IntPtr.Zero,
                UrlReference = IntPtr.Zero,
                ProvFlags = WtdSaferFlag,
                UiContext = 0,
                SignatureSettings = IntPtr.Zero,
            };
            Marshal.StructureToPtr(data, dataPointer, false);

            var result = WinVerifyTrust(IntPtr.Zero, ref action, dataPointer);

            // 释放 WinVerifyTrust 内部的校验状态
            data.StateAction = WtdStateActionClose;
            Marshal.StructureToPtr(data, dataPointer, false);
            WinVerifyTrust(IntPtr.Zero, ref action, dataPointer);

            return result;
        }
        catch (Exception ex)
        {
            Log.Warn($"签名校验失败 {path}：{ex.Message}");
            return unchecked((int)0x80004005);
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPointer);
            Marshal.FreeHGlobal(dataPointer);
            Marshal.FreeHGlobal(pathPointer);
        }
    }
}
