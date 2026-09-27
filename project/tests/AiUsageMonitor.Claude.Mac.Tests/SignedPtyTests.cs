using System.Diagnostics;
using System.Runtime.Versioning;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform.Mac;

namespace AiUsageMonitor.Claude.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class SignedPtyTests
{
    [Fact]
    public async Task SignedFakeTraversesLocatorCapabilityPtyVtAndCommonParser()
    {
        using var temp = new Scratch();
        string helper = Path.Combine(AppContext.BaseDirectory, "test-supervisor");
        string executable = Path.Combine(AppContext.BaseDirectory, "fake-claude-macos");
        string journal = Path.Combine(temp.Path, "sessions");
        var policy = new ClaudeLaunchPolicy(temp.Path, Path.Combine(temp.Path, "library"));
        var locator = new MacClaudeExecutableLocator(temp.Path, helper, policy);
        Assert.True(locator.Resolve(executable).SignatureValid);
        var workspace = new AcquisitionTests.Workspace(temp.Path);
        var probe = new MacClaudeCapabilityProbe(new MacManagedProcessLauncher(helper, journal, verifyClaude: true), policy, workspace);
        var source = new ClaudeCliActiveSource(executable, "/unused-bridge", TimeSpan.FromSeconds(5), workspace,
            new MacClaudeScreenSessionFactory(new MacManagedProcessLauncher(helper, journal, usePty: true, verifyClaude: true), policy), locator, probe.ProbeAsync);
        var observation = await source.RefreshAsync(CancellationToken.None);
        Assert.True(observation.Snapshot.Availability == UsageAvailability.Available, observation.Snapshot.Reason);
        Assert.Equal(2, observation.Snapshot.Windows.Count);
        Assert.Empty(Directory.EnumerateDirectories(journal));
    }

    [Fact]
    public async Task ProductionSignatureRejectsAdHocFakeAndTestSignatureRejectsUnrelatedBinary()
    {
        using var temp = new Scratch();
        string executable = Path.Combine(AppContext.BaseDirectory, "fake-claude-macos");
        var policy = new ClaudeLaunchPolicy(temp.Path, temp.Path);
        Assert.Equal("SIGNATURE_VERIFICATION_FAILED", new MacClaudeExecutableLocator(temp.Path,
            Path.Combine(AppContext.BaseDirectory, "ai-usage-process-supervisor"), policy).Resolve(executable).FailureReason);
        var launcher = new MacManagedProcessLauncher(Path.Combine(AppContext.BaseDirectory, "test-supervisor"), Path.Combine(temp.Path, "sessions"), verifyClaude: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<Exception>(() => launcher.StartAsync(new ProcessStartInfo("/bin/echo"), cancel.Token));
        Assert.False(Directory.EnumerateFiles(temp.Path, "processes", SearchOption.AllDirectories).Any());
    }
}
