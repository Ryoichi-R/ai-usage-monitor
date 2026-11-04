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
        string[] candidates = application == DesktopApplication.Claude
            ? ["Claude.app/Contents/MacOS/Claude"]
            : ["Codex.app/Contents/MacOS/Codex", "Codex.app/Contents/MacOS/ChatGPT", "ChatGPT.app/Contents/MacOS/ChatGPT"];
        // A path is only a candidate. Even ChatGPT.app must have the signed Codex identity.
        return candidates.Any(candidate => string.Equals(full, root + candidate, StringComparison.Ordinal));
    }

    public bool IsDesktopAncestor(DesktopApplication application) => OperatingSystem.IsMacOS() &&
        HasDesktopAncestor(Environment.ProcessId, application,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications"),
            ReadIdentity, VerifySignature);

    internal sealed record ProcessIdentity(int Pid, int Parent, ulong StartSeconds, ulong StartMicroseconds, string Path);

    internal static bool HasDesktopAncestor(int pid, DesktopApplication application, string userApplications,
        Func<int, ProcessIdentity?> read, Func<int, DesktopApplication, bool> verify)
    {
        for (int depth = 0; depth < 24 && pid > 1; depth++)
        {
            ProcessIdentity? child = read(pid);
            if (child is null || child.Parent <= 1 || child.Parent == pid) return false;
            pid = child.Parent;
            ProcessIdentity? before = read(pid);
            if (before is null) return false;
            if (Path.GetFileName(before.Path).StartsWith("AiUsageMonitor.App", StringComparison.Ordinal)) return false;
            if (MatchesPath(before.Path, application, "/Applications") || MatchesPath(before.Path, application, userApplications))
                return verify(pid, application) && before == read(pid);
        }
        return false;
    }

    private static ProcessIdentity? ReadIdentity(int pid)
    {
        int size = Marshal.SizeOf<BsdInfo>();
        if (proc_pidinfo(pid, 3, 0, out BsdInfo before, size) != size) return null;
        byte[] buffer = new byte[4096];
        int count = proc_pidpath(pid, buffer, (uint)buffer.Length);
        if (count <= 0 || proc_pidinfo(pid, 3, 0, out BsdInfo after, size) != size ||
            before.ParentId != after.ParentId || before.StartSeconds != after.StartSeconds || before.StartMicroseconds != after.StartMicroseconds)
            return null;
        int end = Array.IndexOf(buffer, (byte)0);
        string path = Encoding.UTF8.GetString(buffer, 0, end >= 0 ? end : Math.Min(count, buffer.Length));
        return new(pid, checked((int)after.ParentId), after.StartSeconds, after.StartMicroseconds, path);
    }

    internal static bool VerifySignature(int pid, DesktopApplication application)
    {
        const string security = "/System/Library/Frameworks/Security.framework/Security";
        const string coreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        nint key = 0, number = 0, attributes = 0, code = 0, text = 0, requirement = 0, library = 0;
        try
        {
            key = CFStringCreateWithCString(0, "pid", 0x08000100);
            number = CFNumberCreate(0, 9, ref pid); // kCFNumberIntType
            if (key == 0 || number == 0) return false;
            // Security looks up a CFString by value, not by our key object's address.
            library = NativeLibrary.Load(coreFoundation);
            attributes = CFDictionaryCreate(0, [key], [number], 1,
                NativeLibrary.GetExport(library, "kCFTypeDictionaryKeyCallBacks"),
                NativeLibrary.GetExport(library, "kCFTypeDictionaryValueCallBacks"));
            string identity = application == DesktopApplication.Claude
                ? "identifier \"com.anthropic.claudefordesktop\" and certificate leaf[subject.OU] = \"Q6L2SF6YDW\""
                : "identifier \"com.openai.codex\" and certificate leaf[subject.OU] = \"2DC432GLL2\"";
            text = CFStringCreateWithCString(0, "anchor apple generic and " + identity, 0x08000100);
            return attributes != 0 && text != 0 &&
                SecRequirementCreateWithString(text, 0, out requirement) == 0 &&
                SecCodeCopyGuestWithAttributes(0, attributes, 0, out code) == 0 &&
                SecCodeCheckValidity(code, 0, requirement) == 0;
        }
        finally
        {
            foreach (nint value in new[] { requirement, text, code, attributes, number, key })
                if (value != 0) CFRelease(value);
            if (library != 0) NativeLibrary.Free(library);
        }

        [DllImport(coreFoundation)] static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
        [DllImport(coreFoundation)] static extern nint CFNumberCreate(nint allocator, int type, ref int value);
        [DllImport(coreFoundation)] static extern nint CFDictionaryCreate(nint allocator, nint[] keys, nint[] values, nint count, nint keyCallbacks, nint valueCallbacks);
        [DllImport(coreFoundation)] static extern void CFRelease(nint value);
        [DllImport(security)] static extern int SecRequirementCreateWithString(nint text, uint flags, out nint requirement);
        [DllImport(security)] static extern int SecCodeCopyGuestWithAttributes(nint host, nint attributes, uint flags, out nint code);
        [DllImport(security)] static extern int SecCodeCheckValidity(nint code, uint flags, nint requirement);
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
