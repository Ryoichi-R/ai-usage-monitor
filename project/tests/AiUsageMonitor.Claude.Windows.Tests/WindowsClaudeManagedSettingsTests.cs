using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Claude.Windows.Process;

namespace AiUsageMonitor.Claude.Windows.Tests;

public sealed class WindowsClaudeManagedSettingsTests
{
    [Fact]
    public void GuardChecksProgramFilesAndUserProfileSources()
    {
        ClaudeManagedSettingsGuard guard = WindowsClaudeManagedSettings.CreateGuard();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(Path.Combine(programFiles, "ClaudeCode", "managed-settings.json"), guard.Paths);
        Assert.Contains(Path.Combine(profile, ".claude", "remote-settings.json"), guard.Paths);
    }

    [Fact]
    public void LocatorRefusesToResolveWhileManagedSettingsArePresent()
    {
        // 実機にmanaged settingsがある場合だけ、locatorが候補の探索前に止まることを確かめる。
        ClaudeExecutableInfo info = ClaudeExecutableLocator.Resolve(null);
        if (!WindowsClaudeManagedSettings.CreateGuard().AllowsLaunch())
            Assert.Equal(ClaudeManagedSettingsGuard.ReasonCode, info.FailureReason);
        else
            Assert.NotEqual(ClaudeManagedSettingsGuard.ReasonCode, info.FailureReason);
    }
}
