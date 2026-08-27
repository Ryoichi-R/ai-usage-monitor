using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Claude.Windows.Cli;
using AiUsageMonitor.Claude.Windows.Process;
using AiUsageMonitor.Platform.Windows;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Windows.Tests;

public sealed class ActualClaudeCliSmokeTests
{
    [Fact]
    public void ClaudeStatusLineBridgeExistsAtExpectedOutputPath()
    {
        Assert.True(File.Exists(GetBridgePath()));
    }

    [WindowsFact]
    [Trait("Category", "Manual")]
    public async Task ApprovedOfficialCliReturnsTwoStrictlyParsedWindows()
    {
        string? executable = Environment.GetEnvironmentVariable(
            "CODEX_USAGE_MONITOR_ACTUAL_CLAUDE");
        string? helper = Environment.GetEnvironmentVariable(
            "CODEX_USAGE_MONITOR_ACTUAL_HELPER");
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(helper))
            return;

        string bridge = GetBridgePath();
        int timeoutSeconds = int.TryParse(
            Environment.GetEnvironmentVariable(
                "CODEX_USAGE_MONITOR_ACTUAL_STARTUP_TIMEOUT_SECONDS"),
            out int configuredTimeout)
            ? Math.Clamp(configuredTimeout, 1, 120)
            : 20;
        var source = new ClaudeCliActiveSource(
            executable,
            bridge,
            TimeSpan.FromSeconds(timeoutSeconds),
            new ClaudeWorkspaceProvisioner(new WindowsAppPathProvider()),
            new WindowsClaudeScreenSessionFactory(helper),
            new ClaudeExecutableLocator());

        ClaudeUsageObservation result = await source.RefreshAsync(CancellationToken.None);

        Assert.True(
            result.Availability == UsageAvailability.Available,
            $"Expected Available, got {result.Availability}: {result.Reason}");
        Assert.Equal(ClaudeUsageSourceKind.CliScreen, result.Source);
        Assert.Equal([300, 10080], result.Snapshot.Windows.Select(window => window.WindowDurationMins));
        Assert.All(
            result.Snapshot.Windows,
            window => Assert.InRange(window.UsedPercent, 0, 100));
    }

    private static string GetBridgePath() =>
        Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "assets", "claude-statusline-bridge.ps1"));
}
