using System.IO;

namespace AiUsageMonitor.TestSupport;

public static class FakeExecutableLocator
{
    private const string ArtifactsRootEnvironmentVariable = "AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT";

    public static string FindCodexFakeAppServer() =>
        Find(projectName: "AiUsageMonitor.FakeAppServer", tfmSegment: "net10.0", displayName: "Fake app-server");

    public static string FindClaudeFakeCli() =>
        Find(projectName: "AiUsageMonitor.FakeClaudeCli", tfmSegment: "net10.0-windows10.0.19041.0", displayName: "Fake Claude CLI");

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
        if (!string.IsNullOrWhiteSpace(artifactsRoot))
        {
            return FindUnderIsolatedArtifactsRoot(artifactsRoot, projectName, executableName, displayName);
        }

        string sourceRelativePath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "support", projectName,
                "bin", configuration, tfmSegment,
                executableName));
        if (!File.Exists(sourceRelativePath))
        {
            throw new InvalidOperationException($"{displayName} was not built: {sourceRelativePath}");
        }
        return sourceRelativePath;
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
