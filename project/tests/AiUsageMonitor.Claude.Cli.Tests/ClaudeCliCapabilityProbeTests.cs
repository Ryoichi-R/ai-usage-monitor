using System.IO;
using AiUsageMonitor.TestSupport;

namespace AiUsageMonitor.Claude.Cli.Tests;

public sealed class ClaudeCliCapabilityProbeTests
{
    [Theory]
    [InlineData("capability-missing", "REQUIRED_FLAG_MISSING")]
    [InlineData("capability-oversized", "CAPABILITY_FAILED")]
    public async Task RejectsIncompleteOrExcessiveFakeCliHelp(string mode, string reason)
    {
        string fake = CopyFakeWithMode(mode);
        try
        {
            ClaudeCliCapabilities result = await ClaudeCliCapabilityProbe.ProbeAsync(
                fake, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.False(result.Supported);
            Assert.Equal(reason, result.FailureReason);
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Fact]
    public async Task TimesOutAndStopsHangingFakeCli()
    {
        string fake = CopyFakeWithMode("capability-hang");
        try
        {
            ClaudeCliCapabilities result = await ClaudeCliCapabilityProbe.ProbeAsync(
                fake, TimeSpan.FromMilliseconds(300), CancellationToken.None);
            Assert.False(result.Supported);
            Assert.Equal("CAPABILITY_TIMEOUT", result.FailureReason);
        }
        finally
        {
            File.Delete(fake);
        }
    }

    private static string CopyFakeWithMode(string mode)
    {
        string source = FakeExecutableLocator.FindClaudeFakeCli();
        string destination = Path.Combine(
            Path.GetDirectoryName(source)!,
            mode + "-" + Guid.NewGuid().ToString("N") + Path.GetExtension(source));
        File.Copy(source, destination);
        return destination;
    }
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
