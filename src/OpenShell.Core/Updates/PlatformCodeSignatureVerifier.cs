using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace OpenShell.Updates;

/// <summary>
/// 默认 <see cref="ICodeSignatureVerifier"/> 实现: 调用平台原生 API 校验代码签名。Per ADR-0037 §5.
/// <list type="bullet">
///   <item>Windows: P/Invoke <c>wintrust.dll!WinVerifyTrust</c> (Authenticode).</item>
///   <item>macOS: 调用系统 <c>codesign --verify --deep --strict</c> (Developer ID 签名校验).</item>
///   <item>Linux / 其他: 无平台标准, 返回 true (no-op) 并附 TODO 注释。</item>
/// </list>
/// 所有 P/Invoke 都用 <see cref="OperatingSystem.IsWindows()"/> / <see cref="OperatingSystem.IsMacOS()"/> 守卫,
/// 避免在非目标平台加载失败。
/// </summary>
public sealed class PlatformCodeSignatureVerifier : ICodeSignatureVerifier
{
    /// <inheritdoc />
    public async Task<bool> VerifyAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(filePath)) return false;

        if (OperatingSystem.IsWindows())
        {
            return VerifyWindowsAuthenticode(filePath);
        }
        else if (OperatingSystem.IsMacOS())
        {
            return await VerifyMacOSCodeSignatureAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Linux 无平台标准代码签名机制, 按设计返回 true (no-op)。
            // 包内容完整性已由 SHA256 ( caller ) + Ed25519 (provider packages) 校验。
            return true;
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task<bool> VerifyMacOSCodeSignatureAsync(string filePath, CancellationToken cancellationToken)
    {
        // 使用 ArgumentList, 避免路径中的空格或 shell 字符改变校验目标。
        var psi = new ProcessStartInfo
        {
            FileName = "/usr/bin/codesign",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--verify");
        psi.ArgumentList.Add("--deep");
        psi.ArgumentList.Add("--strict");
        psi.ArgumentList.Add(filePath);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // codesign 缺失时必须拒绝, 不能把“无法校验”当成“已签名”。
            return false;
        }
    }

    // ===== Windows Authenticode (WinVerifyTrust) =====

    [SupportedOSPlatform("windows")]
    private static bool VerifyWindowsAuthenticode(string filePath)
    {
        // WinVerifyTrust 通过文件路径 + WINTRUST_DATA 结构触发 Authenticode / catalog 校验。
        // 这里走 Authenticode 路径: 设置 dwUIChoice = WTD_UI_NONE, fdwRevocationChecks = WTD_REVOKE_NONE,
        // dwUnionChoice = WTD_CHOICE_FILE, 调用一次 WinVerifyTrust 验证 PE 签名。
        var actionVerify = new Guid(WINTRUST_ACTION_GENERIC_VERIFY_V2);
        var filePathPtr = Marshal.StringToHGlobalUni(filePath);
        var fileInfoPtr = IntPtr.Zero;
        try
        {
            var fileData = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwsFilePath = filePathPtr,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero,
            };
            fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(fileData, fileInfoPtr, fDeleteOld: false);

            var trustData = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = fileInfoPtr,
                dwStateAction = WTD_STATEACTION_IGNORE,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
            };

            // WinVerifyTrust 返回 0 表示签名有效且受信任。
            int hr = WinVerifyTrust(IntPtr.Zero, ref actionVerify, ref trustData);

            // 显式关闭状态句柄 (本次调用未启用 state action, 但保持对称)。
            trustData.dwStateAction = WTD_STATEACTION_CLOSE;
            _ = WinVerifyTrust(IntPtr.Zero, ref actionVerify, ref trustData);

            return hr == 0;
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero)
            {
                try { Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr); } catch { }
                Marshal.FreeHGlobal(fileInfoPtr);
            }
            Marshal.FreeHGlobal(filePathPtr);
        }
    }

    // ===== WinTrust P/Invoke 常量与结构 =====

    private const string WinTrustDll = "wintrust.dll";
    private const string WINTRUST_ACTION_GENERIC_VERIFY_V2 = "00AAC56B-CD44-11d0-8CC2-00C04FC295EE";
    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_IGNORE = 0;
    private const uint WTD_STATEACTION_CLOSE = 2;

    [DllImport(WinTrustDll, CharSet = CharSet.Unicode, SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    [SupportedOSPlatform("windows")]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwsFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    [SupportedOSPlatform("windows")]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
