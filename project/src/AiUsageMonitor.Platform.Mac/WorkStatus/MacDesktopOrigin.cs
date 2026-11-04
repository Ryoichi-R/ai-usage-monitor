using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Mac.WorkStatus;

[SupportedOSPlatform("macos")]
public sealed class MacDesktopOrigin : IDesktopOrigin
{
    public static bool MatchesPath(string path, DesktopApplication application, string applicationsRoot)
    {
        string root = Path.GetFullPath(applicationsRoot).TrimEnd('/') + "/";
        string full = Path.GetFullPath(path);
        string[] names = application == DesktopApplication.Claude ? ["Claude"] : ["Codex", "ChatGPT"];
        // The bundle path is evidence of origin, not authentication of the application.
        // Codex never accepts the ordinary ChatGPT.app bundle as Codex Desktop.
        string bundle = application == DesktopApplication.Claude ? "Claude" : "Codex";
        return names.Any(name => string.Equals(full, root + bundle + ".app/Contents/MacOS/" + name, StringComparison.Ordinal));
    }

    public bool IsDesktopAncestor(DesktopApplication application)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        int pid = Environment.ProcessId;
        int size = Marshal.SizeOf<BsdInfo>();
        for (int depth = 0; depth < 24 && pid > 1; depth++)
        {
            if (proc_pidinfo(pid, 3, 0, out BsdInfo child, size) != size || child.ParentId <= 1 || child.ParentId == pid) return false;
            pid = checked((int)child.ParentId);
            if (proc_pidinfo(pid, 3, 0, out BsdInfo before, size) != size) return false;
            byte[] buffer = new byte[4096];
            int count = proc_pidpath(pid, buffer, (uint)buffer.Length);
            if (count <= 0) return false;
            if (proc_pidinfo(pid, 3, 0, out BsdInfo after, size) != size ||
                before.StartSeconds != after.StartSeconds || before.StartMicroseconds != after.StartMicroseconds) return false;
            int end = Array.IndexOf(buffer, (byte)0);
            string path = Encoding.UTF8.GetString(buffer, 0, end >= 0 ? end : Math.Min(count, buffer.Length));
            if (Path.GetFileName(path).StartsWith("AiUsageMonitor.App", StringComparison.Ordinal)) return false;
            string userApplications = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
            if (MatchesPath(path, application, "/Applications") || MatchesPath(path, application, userApplications)) return true;
        }
        return false;
    }

    // proc_bsdinfo fields and PROC_PIDTBSDINFO are defined by Apple's public proc_info.h.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct BsdInfo
    {
        public uint Flags, Status, ExitStatus, ProcessId, ParentId, Uid, Gid, RealUid, RealGid, SavedUid, SavedGid, Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string Command;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Files, Group, Jobs, Terminal, TerminalGroup;
        public int Nice;
        public ulong StartSeconds, StartMicroseconds;
    }
    [DllImport("/usr/lib/libproc.dylib")] private static extern int proc_pidinfo(int pid, int flavor, ulong argument, out BsdInfo buffer, int size);
    [DllImport("/usr/lib/libproc.dylib")] private static extern int proc_pidpath(int pid, [Out] byte[] buffer, uint size);
}
