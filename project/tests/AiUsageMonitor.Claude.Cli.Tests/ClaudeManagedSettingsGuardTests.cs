using AiUsageMonitor.Claude.Cli;

namespace AiUsageMonitor.Claude.Cli.Tests;

public sealed class ClaudeManagedSettingsGuardTests
{
    [Fact]
    public void AnyPresentFileDirectoryOrPolicyValueBlocksLaunchWithoutReadingIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-managed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file = Path.Combine(root, "managed-settings.json");
            string directory = Path.Combine(root, "managed-settings.d");
            bool policy = false;
            var guard = new ClaudeManagedSettingsGuard([file, directory, Path.Combine(root, "missing", "x.json")], () => policy);
            Assert.True(guard.AllowsLaunch());

            File.WriteAllText(file, "not JSON");
            Assert.False(guard.AllowsLaunch());
            File.Delete(file);
            Directory.CreateDirectory(directory);
            Assert.False(guard.AllowsLaunch());
            Directory.Delete(directory);
            policy = true;
            Assert.False(guard.AllowsLaunch());
            Assert.True(new ClaudeManagedSettingsGuard([]).AllowsLaunch());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WindowsSourcesFollowTheOfficialLocations()
    {
        var guard = ClaudeManagedSettingsGuard.ForWindows("profile", "programs", null, () => false);
        Assert.Equal(
        [
            Path.Combine("profile", ".claude", "remote-settings.json"),
            Path.Combine("programs", "ClaudeCode", "managed-settings.json"),
            Path.Combine("programs", "ClaudeCode", "managed-settings.d"),
            Path.Combine("programs", "ClaudeCode", "managed-mcp.json"),
        ], guard.Paths);
        var moved = ClaudeManagedSettingsGuard.ForWindows("profile", "programs", "config", () => false);
        Assert.Equal(Path.Combine("config", "remote-settings.json"), moved.Paths[^1]);
        Assert.False(ClaudeManagedSettingsGuard.ForWindows("profile", "programs", null, () => true).AllowsLaunch());
    }

    [Fact]
    public void MacSourcesIncludeManagedPreferencesForSystemAndUser()
    {
        var guard = ClaudeManagedSettingsGuard.ForMacOS("/Users/someone", "/Library");
        // Build the expected paths with the host separator so the shared test also runs on Windows.
        Assert.Contains(Path.Combine("/Users/someone", ".claude", "remote-settings.json"), guard.Paths);
        Assert.Contains(Path.Combine("/Library", "Application Support", "ClaudeCode", "managed-settings.d"), guard.Paths);
        Assert.Contains(Path.Combine("/Library", "Managed Preferences", "someone", "com.anthropic.claudecode.plist"), guard.Paths);
        Assert.Contains(Path.Combine("/Users/someone", "Library", "Managed Preferences", "com.anthropic.claudecode.plist"), guard.Paths);
        Assert.Equal(7, guard.Paths.Count);
        Assert.Equal("MANAGED_SETTINGS_PRESENT", ClaudeManagedSettingsGuard.ReasonCode);
    }
}
