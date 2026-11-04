using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Windows.WorkStatus;

[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopOrigin : IDesktopOrigin
{
    public static bool MatchesPath(string path, DesktopApplication application, string packageRoot)
    {
        string parent = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;
        string relative = Path.GetRelativePath(parent, full).Replace('\\', '/');
        return application == DesktopApplication.Codex
            ? relative.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase) &&
                (relative.EndsWith("/app/ChatGPT.exe", StringComparison.OrdinalIgnoreCase) || relative.EndsWith("/app/Codex.exe", StringComparison.OrdinalIgnoreCase))
            : relative.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase) && relative.EndsWith("/app/Claude.exe", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsDesktopAncestor(DesktopApplication application)
    {
        if (!OperatingSystem.IsWindows()) return false;
        nint snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == -1) return false;
        try
        {
            var parents = new Dictionary<uint, uint>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) return false;
            do { parents[entry.ProcessId] = entry.ParentProcessId; } while (Process32Next(snapshot, ref entry));
            uint id = (uint)Environment.ProcessId;
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            for (int depth = 0; depth < 24 && parents.TryGetValue(id, out uint parent) && parent != id; depth++)
            {
                id = parent;
                try
                {
                    using var process = System.Diagnostics.Process.GetProcessById((int)id);
                    if (process.MainModule?.FileName is not { } path) continue;
                    if (Path.GetFileNameWithoutExtension(path).StartsWith("AiUsageMonitor.App", StringComparison.OrdinalIgnoreCase)) return false;
                    if (MatchesPath(path, application, packages)) return true;
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            return false;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll")] private static extern nint CreateToolhelp32Snapshot(uint flags, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
