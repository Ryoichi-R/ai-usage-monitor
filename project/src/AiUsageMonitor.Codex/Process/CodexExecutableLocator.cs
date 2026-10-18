using System.Runtime.InteropServices;
using System.Text.Json;

namespace AiUsageMonitor.Codex.Process;

/// <summary>探索規則を選ぶOS。既定は実行中のOSで、テストでは両OSの規則を明示して検証する。</summary>
internal enum CodexLocatorPlatform
{
    Windows,
    MacOS,
}

public sealed class CodexExecutableLocator
{
    // ChatGPT.app（macOS）が同梱するCodex CLI。2026-09-27にMac Studioで実物を確認した
    // （codex-cli 0.158.0-alpha.2.1、TeamIdentifier 2DC432GLL2）。bin/codexはこの実体へexecするshell scriptのため、
    // 実体のMach-Oを直接起動する。
    private static readonly string[] MacBundledRelativePaths =
    [
        Path.Combine("ChatGPT.app", "Contents", "Resources", "codex-cli", "CodexCLI.app", "Contents", "MacOS", "codex"),
    ];

    // Finderやlogin itemから起動するとPATHは/usr/bin:/bin:/usr/sbin:/sbin程度になる。
    // Homebrew・npm既定prefixの代表的な配置をPATHとは別に候補へ加える。
    private static readonly string[] MacWellKnownDirectories = ["/opt/homebrew/bin", "/usr/local/bin"];

    public static string? Locate(string? overridePath = null, string? pathEnvironment = null, Architecture? architecture = null) =>
        Locate(
            overridePath,
            pathEnvironment ?? Environment.GetEnvironmentVariable("PATH"),
            architecture ?? RuntimeInformation.ProcessArchitecture,
            OperatingSystem.IsWindows() ? CodexLocatorPlatform.Windows : CodexLocatorPlatform.MacOS,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows() ? null : "/Applications",
            OperatingSystem.IsWindows() ? null : MacWellKnownDirectories);

    internal static string? Locate(
        string? overridePath,
        string? pathEnvironment,
        Architecture architecture,
        CodexLocatorPlatform platform,
        string? userHome,
        string? applicationsDirectory,
        IReadOnlyList<string>? wellKnownDirectories)
    {
        // An explicit executable is authoritative. Never silently run another account's/default CLI.
        if (!string.IsNullOrWhiteSpace(overridePath))
            return platform == CodexLocatorPlatform.Windows
                ? ResolveWindowsCandidate(overridePath, architecture)
                : ResolveMacCandidate(overridePath, architecture);

        if (platform == CodexLocatorPlatform.Windows)
        {
            foreach (string candidate in WindowsCandidates(overridePath, pathEnvironment))
            {
                string? resolved = ResolveWindowsCandidate(candidate, architecture);
                if (resolved is not null) return resolved;
            }
            return LocateDesktopExecutable(architecture);
        }

        foreach (string candidate in MacCandidates(overridePath, pathEnvironment, userHome, applicationsDirectory, wellKnownDirectories))
        {
            string? resolved = ResolveMacCandidate(candidate, architecture);
            if (resolved is not null) return resolved;
        }
        return null;
    }

    private static IEnumerable<string> WindowsCandidates(string? overridePath, string? pathEnvironment)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath;
        foreach (string directory in SplitPath(pathEnvironment))
        {
            foreach (string name in new[] { "codex.exe", "codex.cmd", "codex.ps1", "codex" })
                yield return Path.Combine(directory, name);
        }
    }

    private static IEnumerable<string> MacCandidates(
        string? overridePath,
        string? pathEnvironment,
        string? userHome,
        string? applicationsDirectory,
        IReadOnlyList<string>? wellKnownDirectories)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)) yield return overridePath;
        foreach (string directory in SplitPath(pathEnvironment))
            yield return Path.Combine(directory, "codex");
        foreach (string directory in wellKnownDirectories ?? [])
            yield return Path.Combine(directory, "codex");
        foreach (string relative in MacBundledRelativePaths)
        {
            if (!string.IsNullOrWhiteSpace(applicationsDirectory))
                yield return Path.Combine(applicationsDirectory, relative);
            if (!string.IsNullOrWhiteSpace(userHome))
                yield return Path.Combine(userHome, "Applications", relative);
        }
    }

    private static string[] SplitPath(string? pathEnvironment) =>
        (pathEnvironment ?? string.Empty).Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ResolveWindowsCandidate(string candidate, Architecture architecture)
    {
        string? full = TryGetFullPath(candidate);
        if (full is null || !File.Exists(full) || IsNetworkPath(full)) return null;
        if (string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase)) return full;

        string packageRoot = Path.Combine(Path.GetDirectoryName(full)!, "node_modules", "@openai", "codex");
        (string packageName, string triple) = architecture switch
        {
            Architecture.X64 => ("@openai/codex-win32-x64", "x86_64-pc-windows-msvc"),
            Architecture.Arm64 => ("@openai/codex-win32-arm64", "aarch64-pc-windows-msvc"),
            _ => (string.Empty, string.Empty),
        };
        return ResolveNpmNative(packageRoot, packageName, [Path.Combine(triple, "bin", "codex.exe")], requireMachO: false);
    }

    private static string? ResolveMacCandidate(string candidate, Architecture architecture)
    {
        string? full = TryGetFullPath(candidate);
        if (full is null || !File.Exists(full)) return null;
        string? target = ResolveFinalTarget(full);
        if (target is null || IsNetworkPath(target)) return null;
        if (IsMachOExecutable(target)) return target;

        // npmのglobal installでは<prefix>/bin/codex → lib/node_modules/@openai/codex/bin/codex.js
        // というsymlinkになる。JS shimはnodeを要求するため、同梱のOS別native binaryを直接起動する。
        string? binDirectory = Path.GetDirectoryName(target);
        string? packageRoot = binDirectory is null ? null : Path.GetDirectoryName(binDirectory);
        if (packageRoot is null) return null;
        (string packageName, string triple) = architecture switch
        {
            Architecture.Arm64 => ("@openai/codex-darwin-arm64", "aarch64-apple-darwin"),
            Architecture.X64 => ("@openai/codex-darwin-x64", "x86_64-apple-darwin"),
            _ => (string.Empty, string.Empty),
        };
        // vendor配下の配置はnpm版の世代で異なりうるため（macOS実物は未検証）、両方を候補にする。
        return ResolveNpmNative(
            packageRoot,
            packageName,
            [Path.Combine(triple, "bin", "codex"), Path.Combine(triple, "codex", "codex")],
            requireMachO: true);
    }

    private static string? ResolveNpmNative(
        string packageRoot,
        string packageName,
        IReadOnlyList<string> vendorRelativePaths,
        bool requireMachO)
    {
        if (packageName.Length == 0 || !ValidPackage(packageRoot, "@openai/codex")) return null;
        string packageDirectoryName = packageName[(packageName.LastIndexOf('/') + 1)..];
        foreach (string root in new[]
        {
            Path.Combine(packageRoot, "node_modules", "@openai", packageDirectoryName),
            Path.Combine(Path.GetDirectoryName(packageRoot)!, packageDirectoryName),
        })
        {
            if (!ValidPackage(root, packageName) && !ValidPackage(root, "@openai/codex")) continue;
            foreach (string relative in vendorRelativePaths)
            {
                string native = Path.GetFullPath(Path.Combine(root, "vendor", relative));
                if (!File.Exists(native)) continue;
                if (requireMachO && !IsMachOExecutable(native)) continue;
                return native;
            }
        }
        return null;
    }

    private static string? TryGetFullPath(string candidate)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidate)); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? ResolveFinalTarget(string path)
    {
        try
        {
            FileSystemInfo? target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target is null ? path : Path.GetFullPath(target.FullName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // 64-bit Mach-O（MH_MAGIC_64）とuniversal binary（FAT_MAGIC / FAT_MAGIC_64）で、実行権限を持つものだけを受け付ける。
    // shell scriptやJS shimを誤って直接起動しないための判定で、署名の検証ではない。
    internal static bool IsMachOExecutable(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows() &&
                (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
            {
                return false;
            }
            using FileStream stream = File.OpenRead(path);
            Span<byte> header = stackalloc byte[4];
            if (stream.ReadAtLeast(header, 4, throwOnEndOfStream: false) != 4) return false;
            uint little = (uint)(header[0] | header[1] << 8 | header[2] << 16 | header[3] << 24);
            uint big = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
            return little == 0xFEEDFACF || big == 0xCAFEBABE || big == 0xCAFEBABF;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsNetworkPath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return true;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string? root = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            // Unixのpath rootは常に"/"のため、所属するmount pointを最長一致で求める。
            DriveInfo? mount = DriveInfo.GetDrives()
                .Where(drive => IsUnderMount(path, drive.RootDirectory.FullName))
                .MaxBy(drive => drive.RootDirectory.FullName.Length);
            return mount?.DriveType == DriveType.Network;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    internal static bool IsUnderMount(string path, string mountPoint)
    {
        string root = mountPoint.TrimEnd('/');
        return root.Length == 0 ||
            path.Equals(root, StringComparison.Ordinal) ||
            path.StartsWith(root + "/", StringComparison.Ordinal);
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
