namespace AiUsageMonitor.TestSupport;

internal static class WorkStatusTestDirectory
{
    public static string Create(string prefix)
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT")
            ?? Path.GetTempPath();
        string path = Path.Combine(ResolveDirectory(new DirectoryInfo(parent)), prefix + "-" + Guid.NewGuid().ToString("N"));
        return Directory.CreateDirectory(path).FullName;
    }

    private static string ResolveDirectory(DirectoryInfo directory)
    {
        if (directory.Parent is not { } parent) return directory.FullName;
        var candidate = new DirectoryInfo(Path.Combine(ResolveDirectory(parent), directory.Name));
        return candidate.Exists && candidate.ResolveLinkTarget(returnFinalTarget: true) is DirectoryInfo target
            ? ResolveDirectory(target)
            : candidate.FullName;
    }
}
