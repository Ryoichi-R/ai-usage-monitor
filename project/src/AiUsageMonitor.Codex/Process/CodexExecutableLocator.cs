using System.Runtime.InteropServices;
using System.Text.Json;

namespace AiUsageMonitor.Codex.Process;

public sealed class CodexExecutableLocator
{
    public static string? Locate(string? overridePath = null, string? pathEnvironment = null, Architecture? architecture = null)
    {
        architecture ??= RuntimeInformation.ProcessArchitecture;
        foreach (string candidate in Candidates(overridePath, pathEnvironment ?? Environment.GetEnvironmentVariable("PATH")))
        {
            string? resolved = ResolveCandidate(candidate, architecture.Value);
            if (resolved is not null) return resolved;
        }
        return LocateDesktopExecutable(architecture.Value);
    }

    private static IEnumerable<string> Candidates(string? overridePath, string? pathEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath;
        foreach (string directory in (pathEnvironment ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (string name in new[] { "codex.exe", "codex.cmd", "codex.ps1", "codex" })
                yield return Path.Combine(directory, name);
        }
    }

    private static string? ResolveCandidate(string candidate, Architecture architecture)
    {
        string full;
        try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        if (!File.Exists(full) || IsNetworkPath(full)) return null;
        if (string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)) return full;

        string packageRoot = Path.Combine(Path.GetDirectoryName(full)!, "node_modules", "@openai", "codex");
        if (!ValidPackage(packageRoot, "@openai/codex")) return null;
        (string packageName, string relative) = architecture switch
        {
            Architecture.X64 => ("@openai/codex-win32-x64", "x86_64-pc-windows-msvc"),
            Architecture.Arm64 => ("@openai/codex-win32-arm64", "aarch64-pc-windows-msvc"),
            _ => (string.Empty, string.Empty),
        };
        if (packageName.Length == 0) return null;
        string packageDirectoryName = packageName[(packageName.LastIndexOf('/') + 1)..];
        foreach (string root in new[] { Path.Combine(packageRoot, "node_modules", "@openai", packageDirectoryName), Path.Combine(Path.GetDirectoryName(packageRoot)!, packageDirectoryName) })
        {
            if (!ValidPackage(root, packageName) && !ValidPackage(root, "@openai/codex")) continue;
            string native = Path.Combine(root, "vendor", relative, "bin", "codex.exe");
            if (File.Exists(native)) return Path.GetFullPath(native);
        }
        return null;
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        try
        {
            string? root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private static bool ValidPackage(string directory, string expectedName)
    {
        string manifest = Path.Combine(directory, "package.json");
        if (!File.Exists(manifest)) return false;
        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(manifest));
            return json.RootElement.TryGetProperty("name", out JsonElement name) && name.GetString() == expectedName;
        }
        catch (JsonException) { return false; }
    }

    private static string? LocateDesktopExecutable(Architecture architecture)
    {
        string? local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local)) return null;
        string arch = architecture == Architecture.Arm64 ? "aarch64-pc-windows-msvc" : "x86_64-pc-windows-msvc";
        string basePath = Path.Combine(local, "Programs", "OpenAI Codex");
        if (!Directory.Exists(basePath)) return null;
        return Directory.EnumerateFiles(basePath, "codex.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains(arch, StringComparison.OrdinalIgnoreCase));
    }
}
