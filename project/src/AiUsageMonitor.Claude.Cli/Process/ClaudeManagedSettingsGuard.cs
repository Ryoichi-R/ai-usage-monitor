namespace AiUsageMonitor.Claude.Cli;

/// <summary>
/// managed settingsが存在する環境ではClaude CLIを起動しない（ADR 003、D13）。
/// 判定はfile・directory・policy値の有無だけで行い、内容は読まない。存在を確認できない場合もfail-closedにする。
/// 配置は公式文書（https://code.claude.com/docs/en/managed-settings.md、server-managed-settings.md）で確認したもの。
/// </summary>
public sealed class ClaudeManagedSettingsGuard
{
    public const string ReasonCode = "MANAGED_SETTINGS_PRESENT";

    private readonly IReadOnlyList<string> _paths;
    private readonly Func<bool>? _policyValuePresent;

    /// <param name="paths">存在すれば起動しないfileまたはdirectory。</param>
    /// <param name="policyValuePresent">OSのpolicy store（Windows registry等）に値があればtrue。確認できなければtrueを返すこと。</param>
    public ClaudeManagedSettingsGuard(IReadOnlyList<string> paths, Func<bool>? policyValuePresent = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _policyValuePresent = policyValuePresent;
    }

    public IReadOnlyList<string> Paths => _paths;

    public bool AllowsLaunch()
    {
        foreach (string path in _paths)
        {
            try { _ = File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        }
        return _policyValuePresent?.Invoke() != true;
    }

    /// <summary>Windowsのfile-based source、server-managed settingsの保存file。registryは呼出し側が渡す。</summary>
    public static ClaudeManagedSettingsGuard ForWindows(string userProfile, string programFiles, string? claudeConfigDirectory, Func<bool> policyValuePresent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(programFiles);
        string system = Path.Combine(programFiles, "ClaudeCode");
        var paths = new List<string>
        {
            Path.Combine(userProfile, ".claude", "remote-settings.json"),
            Path.Combine(system, "managed-settings.json"),
            Path.Combine(system, "managed-settings.d"),
            Path.Combine(system, "managed-mcp.json"),
        };
        // CLAUDE_CONFIG_DIRで設定directoryを移している場合、server-managed settingsの保存先も移りうる。
        if (!string.IsNullOrWhiteSpace(claudeConfigDirectory))
            paths.Add(Path.Combine(claudeConfigDirectory, "remote-settings.json"));
        return new ClaudeManagedSettingsGuard(paths, policyValuePresent);
    }

    /// <summary>macOSのfile-based source、managed preferences、server-managed settingsの保存file。</summary>
    public static ClaudeManagedSettingsGuard ForMacOS(string home, string systemLibrary = "/Library")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemLibrary);
        string system = Path.Combine(systemLibrary, "Application Support", "ClaudeCode");
        return new ClaudeManagedSettingsGuard(
        [
            Path.Combine(home, ".claude", "remote-settings.json"),
            Path.Combine(system, "managed-settings.json"),
            Path.Combine(system, "managed-settings.d"),
            Path.Combine(system, "managed-mcp.json"),
            Path.Combine(systemLibrary, "Managed Preferences", "com.anthropic.claudecode.plist"),
            Path.Combine(systemLibrary, "Managed Preferences", Path.GetFileName(home), "com.anthropic.claudecode.plist"),
            Path.Combine(home, "Library", "Managed Preferences", "com.anthropic.claudecode.plist"),
        ]);
    }
}
