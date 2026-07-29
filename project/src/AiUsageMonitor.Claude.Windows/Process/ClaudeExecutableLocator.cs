using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AiUsageMonitor.Claude.Windows.Process;

public sealed record ClaudeExecutableInfo(
    string Path,
    string? Version,
    bool SignatureValid,
    string? Publisher,
    string? FailureReason);

/// <summary>公式install経路から署名済みnative Claude CLIを解決する。</summary>
public sealed class ClaudeExecutableLocator
{
    public static ClaudeExecutableInfo Resolve(string? configuredPath)
    {
        if (!OperatingSystem.IsWindows())
            return Failure("UNSUPPORTED_PLATFORM");

        IEnumerable<string> candidates = string.IsNullOrWhiteSpace(configuredPath)
            ? DefaultCandidates()
            : [configuredPath];
        foreach (string candidate in candidates)
        {
            string fullPath;
            try { fullPath = System.IO.Path.GetFullPath(candidate); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }
            if (!File.Exists(fullPath) || IsNetworkPath(fullPath))
                continue;

            (bool valid, string? publisher) = AuthenticodeVerifier.Verify(fullPath);
            if (!valid || !string.Equals(publisher, "Anthropic, PBC", StringComparison.OrdinalIgnoreCase))
                return new(fullPath, FileVersion(fullPath), false, publisher, "UNTRUSTED_EXECUTABLE");
            return new(fullPath, FileVersion(fullPath), true, publisher, null);
        }
        return Failure("CLAUDE_NOT_INSTALLED");
    }

    public static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        try
        {
            string? root = System.IO.Path.GetPathRoot(path);
            return root is not null && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private static IEnumerable<string> DefaultCandidates()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return System.IO.Path.Combine(profile, ".local", "bin", "claude.exe");
        yield return System.IO.Path.Combine(local, "Programs", "claude", "claude.exe");
        yield return System.IO.Path.Combine(local, "ClaudeCode", "claude.exe");
    }

    private static string? FileVersion(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).ProductVersion; }
        catch (Exception exception) when (exception is FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ClaudeExecutableInfo Failure(string reason) =>
        new(string.Empty, null, false, null, reason);
}

internal static class AuthenticodeVerifier
{
    private static readonly Guid GenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static (bool Valid, string? Publisher) Verify(string path)
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
            return (valid, publisher);
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
