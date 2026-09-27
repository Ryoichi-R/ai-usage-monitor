using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AiUsageMonitor.Claude.Mac;

[SupportedOSPlatform("macos")]
internal static class LocalSocketSecurity
{
    internal static string DefaultPath => $"/private/tmp/aiusage-statusline-{GetUid()}/v1.sock";
    internal static bool Valid(string path, bool directory) => Validate(path, directory ? 1 : 0) == 0;
    internal static bool SameUser(Socket socket) => Peer(socket.Handle.ToInt32()) == 0;
    [DllImport("libaiusage-local-socket.dylib", EntryPoint = "aiusage_validate", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int Validate([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int directory);
    [DllImport("libaiusage-local-socket.dylib", EntryPoint = "aiusage_lock_directory", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    internal static extern int LockDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport("libaiusage-local-socket.dylib", EntryPoint = "aiusage_peer")]
    private static extern int Peer(int descriptor);
    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();
    [DllImport("libc", EntryPoint = "close")]
    internal static extern int Close(int descriptor);
}
