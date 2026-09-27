using System.IO;

namespace AiUsageMonitor.TestSupport;

public static class FakeExecutableLocator
{
    private const string ArtifactsRootEnvironmentVariable = "AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT";

    public static string FindCodexFakeAppServer() =>
        Find(projectName: "AiUsageMonitor.FakeAppServer", tfmSegment: "net10.0", displayName: "Fake app-server");

    public static string FindClaudeFakeCli() =>
        Find(projectName: "AiUsageMonitor.FakeClaudeCli", tfmSegment: "net10.0", displayName: "Fake Claude CLI");

    /// <summary>
    /// ConsoleHelperClientが起動するhost役の実行ファイル。CLI本体の模擬とは別プロセスであり、
    /// Windows専用のconsole helper protocolだけを提供する。
    /// </summary>
    public static string FindClaudeFakeConsoleHelperHost() =>
        Find(
            projectName: "AiUsageMonitor.FakeClaudeConsoleHelperHost",
            tfmSegment: "net10.0-windows10.0.19041.0",
            displayName: "Fake Claude console helper host");

    public static string FindTopmostTestHost() =>
        Find(
            projectName: "AiUsageMonitor.TopmostTestHost",
            tfmSegment: "net10.0-windows10.0.19041.0",
            displayName: "TopMost test host",
            configuration: CurrentTestConfiguration);

    private static string Find(
        string projectName,
        string tfmSegment,
        string displayName,
        string configuration = "Release")
    {
        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string executableName = projectName + extension;
        string? artifactsRoot = Environment.GetEnvironmentVariable(ArtifactsRootEnvironmentVariable);
        string executablePath;
        if (!string.IsNullOrWhiteSpace(artifactsRoot))
        {
            executablePath = FindUnderIsolatedArtifactsRoot(artifactsRoot, projectName, executableName, displayName);
        }
        else
        {
            executablePath = Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..", "..", "..", "..",
                    "support", projectName,
                    "bin", configuration, tfmSegment,
                    executableName));
            if (!File.Exists(executablePath))
            {
                throw new InvalidOperationException($"{displayName} was not built: {executablePath}");
            }
        }

        // The current Codex locator treats an explicit .exe path as a direct executable.
        // Keep portable fake-appserver integration tests usable on macOS until the production
        // locator gains its darwin-native path handling in Phase 4. A Mach-O apphost remains
        // executable through this test-only symlink regardless of its filename suffix.
        if (OperatingSystem.IsMacOS() && projectName == "AiUsageMonitor.FakeAppServer")
        {
            string aliasPath = executablePath + ".exe";
            if (!File.Exists(aliasPath))
            {
                try
                {
                    File.CreateSymbolicLink(aliasPath, Path.GetFileName(executablePath));
                }
                catch (IOException) when (File.Exists(aliasPath))
                {
                    // Another parallel test created the same alias.
                }
            }
            return aliasPath;
        }

        return executablePath;
    }

#if DEBUG
    private const string CurrentTestConfiguration = "Debug";
#else
    private const string CurrentTestConfiguration = "Release";
#endif

    private static string FindUnderIsolatedArtifactsRoot(
        string artifactsRoot,
        string projectName,
        string executableName,
        string displayName)
    {
        if (!Path.IsPathFullyQualified(artifactsRoot))
        {
            throw new InvalidOperationException(
                $"{ArtifactsRootEnvironmentVariable} must be an absolute path: {artifactsRoot}");
        }

        string isolatedRoot = Path.GetFullPath(artifactsRoot);
        string projectBinRoot = Path.GetFullPath(Path.Combine(isolatedRoot, "bin", projectName));
        string expectedCanonicalPath = Path.Combine(projectBinRoot, "<build-pivot>", executableName);

        if (!Directory.Exists(projectBinRoot))
        {
            throw new InvalidOperationException(
                $"{displayName} was not found under isolated artifacts root (missing directory): {expectedCanonicalPath}");
        }

        var candidates = Directory.EnumerateFiles(projectBinRoot, executableName, SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(candidate => IsWithinRoot(candidate, projectBinRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 1)
        {
            return candidates[0];
        }

        if (candidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"{displayName} is ambiguous under isolated artifacts root: found {candidates.Length} candidates " +
                $"for {executableName} under {projectBinRoot}. Build only the configuration under test into this root.");
        }

        throw new InvalidOperationException(
            $"{displayName} was not found under isolated artifacts root: {expectedCanonicalPath}");
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return candidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
