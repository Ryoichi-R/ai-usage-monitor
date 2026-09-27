using AiUsageMonitor.TestSupport;

namespace AiUsageMonitor.Claude.Cli.Tests;

public sealed class ClaudeCliCapabilityProbeTests
{
    [Fact]
    public async Task AcceptsPortableFakeCliWithRequiredFlagsAndVersion()
    {
        ClaudeCliCapabilities result = await ClaudeCliCapabilityProbe.ProbeAsync(
            FakeExecutableLocator.FindClaudeFakeCli(),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(result.Supported);
        Assert.Equal("2.1.218 (fake)", result.Version);
        Assert.Null(result.FailureReason);
    }
}
