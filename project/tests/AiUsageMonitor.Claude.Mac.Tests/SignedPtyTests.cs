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
        var candidate = locator.Resolve(executable);
        Assert.True(candidate.SignatureValid);
        var workspace = new AcquisitionTests.Workspace(temp.Path);
        var probe = new MacClaudeCapabilityProbe(new MacManagedProcessLauncher(helper, journal, verifyClaude: true, expectedIdentity: () => candidate.Identity), policy, workspace);
        var source = new ClaudeCliActiveSource(executable, "/unused-bridge", TimeSpan.FromSeconds(5), workspace,
            new MacClaudeScreenSessionFactory(new MacManagedProcessLauncher(helper, journal, usePty: true, verifyClaude: true, expectedIdentity: () => candidate.Identity), policy), locator, probe.ProbeAsync);
        var observation = await source.RefreshAsync(CancellationToken.None);
        Assert.True(observation.Snapshot.Availability == UsageAvailability.Available, observation.Snapshot.Reason);
        Assert.Equal(2, observation.Snapshot.Windows.Count);
        Assert.Empty(Directory.EnumerateDirectories(journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VersionedSupervisorReportsChildExitIndependently(bool nonzero)
    {
        using var temp = new Scratch();
        string helper = Path.Combine(AppContext.BaseDirectory, "test-supervisor");
        string executable = Path.Combine(AppContext.BaseDirectory, "fake-claude-macos");
        var candidate = new MacClaudeExecutableLocator(temp.Path, helper, new(temp.Path, temp.Path)).Resolve(executable);
        var launcher = new MacManagedProcessLauncher(helper, Path.Combine(temp.Path, "sessions"), verifyClaude: true, expectedIdentity: () => candidate.Identity);
        var info = new ProcessStartInfo(executable);
        info.ArgumentList.Add(nonzero ? "--exit-42" : "--version");
        var session = await launcher.StartAsync(info, default);
        await session.StandardOutput.ReadToEndAsync();
        await session.DisposeAsync();
        Assert.Equal(Platform.ManagedProcessOutcome.Clean, session.Outcome);
        Assert.Equal(nonzero ? 42 : 0, session.ExitCode);
        Assert.Equal(0, session.SupervisorError);
        Assert.Null(session.TerminationSignal);
    }

    [Fact]
    public async Task ExpectedIdentityMismatchCannotExecuteCandidate()
    {
        using var temp = new Scratch();
        string helper = Path.Combine(AppContext.BaseDirectory, "test-supervisor");
        string executable = Path.Combine(AppContext.BaseDirectory, "fake-claude-macos");
        var launcher = new MacManagedProcessLauncher(helper, Path.Combine(temp.Path, "sessions"), verifyClaude: true,
            expectedIdentity: () => "2|org.example.ai-usage-suspended-probe||2|abcd");
        var info = new ProcessStartInfo(executable); info.ArgumentList.Add("--marker"); info.ArgumentList.Add(Path.Combine(temp.Path, "must-not-exist"));
        var error = await Assert.ThrowsAsync<MacProcessStartException>(() => launcher.StartAsync(info, default));
        Assert.True(error.CleanupConfirmed); Assert.Equal("CLI_IDENTITY_MISMATCH", error.Message);
        Assert.False(File.Exists(Path.Combine(temp.Path, "must-not-exist")));
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
