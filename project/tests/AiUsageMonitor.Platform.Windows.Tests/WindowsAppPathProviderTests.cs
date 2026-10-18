namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsAppPathProviderTests
{
    [Fact]
    public void ResolvesEachPathUnderTheLegacyAppDataDirectoryName()
    {
        var provider = new WindowsAppPathProvider();

        Assert.EndsWith(Path.Combine("CodexUsageMonitor", "settings.json"), provider.SettingsFilePath);
        Assert.EndsWith(Path.Combine("CodexUsageMonitor", "ClaudeCliWorkspace"), provider.ClaudeWorkspaceDirectory);
        Assert.EndsWith(Path.Combine("CodexUsageMonitor", "Temp"), provider.TemporaryDirectory);
        Assert.False(string.IsNullOrWhiteSpace(provider.UserHomeDirectory));
    }

    [Fact]
    public void InternalConstructorHonorsTheProvidedLocalApplicationDataRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));

        var provider = new WindowsAppPathProvider(root);

        Assert.Equal(
            Path.Combine(root, "CodexUsageMonitor", "settings.json"),
            provider.SettingsFilePath);
    }
}
