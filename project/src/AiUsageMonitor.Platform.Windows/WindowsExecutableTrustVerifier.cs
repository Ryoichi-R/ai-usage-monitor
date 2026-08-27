using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Windows;

/// <summary>
/// WinVerifyTrustで実行ファイルのAuthenticode署名を検証する。
/// 検証異常・parse不能・署名者取得失敗はすべてValid=falseのfail-closedとする。
/// </summary>
public sealed class WindowsExecutableTrustVerifier : IExecutableTrustVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public ExecutableTrustResult Verify(string path)
    {
        IntPtr fileInfoPointer = IntPtr.Zero;
        try
        {
            var fileInfo = new WinTrustFileInfo(path);
            fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            var data = new WinTrustData(fileInfoPointer);
            Guid action = GenericVerifyV2;
            bool valid = WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
            string? publisher = null;
            if (valid)
            {
                try
                {
#pragma warning disable SYSLIB0057 // Required to read the embedded Authenticode signer from a PE file.
                    using var certificate = new X509Certificate2(
                        X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
                    publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
                }
                catch (CryptographicException)
                {
                    valid = false;
                }
            }
            return new ExecutableTrustResult(valid, publisher, valid ? null : "UNTRUSTED_EXECUTABLE");
        }
        finally
        {
            if (fileInfoPointer != IntPtr.Zero)
            {
                Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
            }
        }
    }

    [DllImport("wintrust.dll", EntryPoint = "WinVerifyTrust", SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr window,
        ref Guid actionId,
        ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;

        public WinTrustFileInfo(string path)
        {
            Size = (uint)Marshal.SizeOf<WinTrustFileInfo>();
            FilePath = path;
            FileHandle = IntPtr.Zero;
            KnownSubject = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
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
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2; // WTD_UI_NONE
            RevocationChecks = 0;
            UnionChoice = 1; // WTD_CHOICE_FILE
            FileInfo = fileInfo;
            StateAction = 0;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x1000; // WTD_CACHE_ONLY_URL_RETRIEVAL
            UiContext = 0;
        }
    }
}
