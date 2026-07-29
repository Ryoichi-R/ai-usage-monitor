using System.Security;

namespace AiUsageMonitor.Core.Settings;

public sealed class CodexHomePathResolver(string? inheritedCodexHome, string userProfilePath)
{
    public string? TryResolveEffectivePath(string? configuredPath)
    {
        string candidate = configuredPath
            ?? (string.IsNullOrWhiteSpace(inheritedCodexHome)
                ? Path.Combine(userProfilePath, ".codex")
                : inheritedCodexHome);
        return TryNormalizeAbsolute(candidate);
    }

    public static string? TryNormalizeAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;
        try
        {
            string root = Path.GetPathRoot(path) ?? string.Empty;
            string normalized = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return normalized.Length < root.Length ? root : normalized;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    public static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
