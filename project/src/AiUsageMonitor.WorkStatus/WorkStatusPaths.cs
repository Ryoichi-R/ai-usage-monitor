namespace AiUsageMonitor.WorkStatus;

public static class WorkStatusPaths
{
    public static string Inside(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string parent = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(parent, full, comparison) && !full.StartsWith(parent + Path.DirectorySeparatorChar, comparison))
            throw new IOException("WORK_STATUS_PATH_OUTSIDE_ROOT");
        RejectLinks(full);
        return full;
    }

    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("WORK_STATUS_LINK_REFUSED");
    }
}
