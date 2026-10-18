using System.Runtime.Versioning;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Claude.Mac;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Mac.Tests;

[SupportedOSPlatform("macos")]
public sealed class AutomaticCompatibilityTests
{
    private const string IdentityA = "2|com.anthropic.claude-code|Q6L2SF6YDW|2|abcdef12";
    private const string IdentityB = "2|com.anthropic.claude-code|Q6L2SF6YDW|2|abcdef34";

    [Theory]
    [InlineData("2|bad!identifier|Q6L2SF6YDW|2|abcdef12")]
    [InlineData("2|com.anthropic.claude-code|bad/team|2|abcdef12")]
    [InlineData("1|com.anthropic.claude-code|Q6L2SF6YDW|2|abcdef12")]
    [InlineData("2|com.anthropic.claude-code|Q6L2SF6YDW|2|xyz")]
    [InlineData("2|com.anthropic.claude-code|Q6L2SF6YDW|none|abcd")]
    public void InvalidIdentityIsRejected(string token) => Assert.Throws<IOException>(() => MacClaudeBinaryIdentity.Parse(token));

    [Fact]
    public void IdentityIsOpaqueAndIndependentOfVersionOrPath()
    {
        Assert.NotEqual(MacClaudeBinaryIdentity.Parse(IdentityA).Key, MacClaudeBinaryIdentity.Parse(IdentityB).Key);
        Assert.Equal(MacClaudeBinaryIdentity.Parse(IdentityA).Key, MacClaudeBinaryIdentity.Parse(IdentityA.ToUpperInvariant().Replace("COM.ANTHROPIC.CLAUDE-CODE", "com.anthropic.claude-code", StringComparison.Ordinal)).Key);
    }

    [Fact]
    public async Task SuccessPublishesOnlyAfterCleanupAndRemovesPending()
    {
        using var fixture = new Fixture();
        bool pendingDuringExecution = false;
        using var source = fixture.Create((report, token) =>
        {
            pendingDuringExecution = fixture.Store.Read().Pending is not null;
            report("claude-cleanup-clean"); return Task.FromResult(Available());
        });
        int progress = 0; source.ProgressChanged += _ => progress++;
        Assert.Equal(UsageAvailability.Available, (await source.RefreshAsync(default)).Availability);
        Assert.True(pendingDuringExecution); Assert.Equal(1, progress);
        Assert.Null(fixture.Store.Read().Pending);
        Assert.Equal(MacClaudeBinaryIdentity.Parse(IdentityA).Key, fixture.Store.Read().LastBinaryId);
    }

    [Theory]
    [InlineData("claude-cleanup-failed", "PROCESS_CLEANUP_FAILED")]
    [InlineData("claude-vt-rejected:csi:7m", "CLI_SCREEN_INCOMPATIBLE")]
    [InlineData("claude-unknown-option", "CLI_ARGUMENT_INCOMPATIBLE")]
    public async Task FailedContractDoesNotPublishValuesAndDoesNotRelaunch(string reportCode, string reason)
    {
        using var fixture = new Fixture(); int calls = 0;
        using var source = fixture.Create((report, token) => { calls++; if (reportCode == "claude-cleanup-failed") report("claude-session-started"); report(reportCode); return Task.FromResult(Available()); });
        Assert.Equal(reason, (await source.RefreshAsync(default)).Reason);
        Assert.Equal(reason, (await source.RefreshAsync(default)).Reason);
        Assert.Equal(1, calls); Assert.Single(fixture.Store.Read().Blocks);
        Assert.Contains(reason, source.DescribeStop());
        source.Recheck(releaseSafety: reportCode == "claude-cleanup-failed");
        await source.RefreshAsync(default); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task BackoffStillInspectsAndNewCandidateRetriesImmediately()
    {
        using var fixture = new Fixture(); int calls = 0;
        using var source = fixture.Create((report, token) => { calls++; return Task.FromResult(Failed()); });
        await source.RefreshAsync(default); Assert.NotNull(source.NextAttemptAt);
        await source.RefreshAsync(default); Assert.Equal(1, calls);
        fixture.Locator.Identity = IdentityB;
        await source.RefreshAsync(default); Assert.Equal(2, calls);
        Assert.True(fixture.Locator.Calls >= 3);
        source.ResetRetry(); await source.RefreshAsync(default); Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CandidateChangeAtPublicationIsTransientAndBlocksNeitherCandidate()
    {
        using var fixture = new Fixture();
        using var source = fixture.Create((report, token) => { fixture.Locator.Identity = IdentityB; return Task.FromResult(Available()); });
        Assert.Equal("CLI_CANDIDATE_CHANGED", (await source.RefreshAsync(default)).Reason);
        Assert.Empty(fixture.Store.Read().Blocks); Assert.Null(fixture.Store.Read().Pending);
    }

    [Fact]
    public async Task SafetyStopSurvivesRestartAndContractChangeButNotDifferentCandidate()
    {
        using var fixture = new Fixture();
        using (var first = fixture.Create((report, token) => { report("claude-session-started"); report("claude-cleanup-failed"); return Task.FromResult(Available()); }))
            await first.RefreshAsync(default);
        using var restarted = fixture.Create((report, token) => Task.FromResult(Available()), "new-contract");
        Assert.Equal("PROCESS_CLEANUP_FAILED", (await restarted.RefreshAsync(default)).Reason);
        restarted.Recheck(false);
        Assert.Equal("PROCESS_CLEANUP_FAILED", (await restarted.RefreshAsync(default)).Reason);
        fixture.Locator.Identity = IdentityB;
        Assert.Equal(UsageAvailability.Available, (await restarted.RefreshAsync(default)).Availability);
    }

    [Fact]
    public async Task UnknownCleanupPreservesPendingAcrossRestartAndRequiresExplicitRelease()
    {
        using var fixture = new Fixture();
        fixture.Store.Write(new() { Pending = new(Guid.NewGuid(), MacClaudeBinaryIdentity.Parse(IdentityA).Key, DateTimeOffset.UtcNow) });
        using var source = fixture.Create((report, token) => throw new InvalidOperationException("must not launch"));
        Assert.Equal("CLI_PREVIOUS_ATTEMPT_INCOMPLETE", (await source.RefreshAsync(default)).Reason);
        Assert.Throws<IOException>(() => source.Recheck(false));
        Assert.Contains("未完了", source.DescribeStop());
        source.Recheck(true); Assert.Null(fixture.Store.Read().Pending);
    }

    [Fact]
    public async Task CancellationAfterCleanCleanupClearsPendingWithoutPublishing()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
        using var source = fixture.Create((report, token) => { report("claude-cleanup-clean"); cancel.Cancel(); return Task.FromResult(Available()); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.RefreshAsync(cancel.Token));
        Assert.Null(fixture.Store.Read().Pending); Assert.Null(fixture.Store.Read().LastBinaryId);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"SchemaVersion\":2,\"Blocks\":[]}")]
    public async Task InvalidStateNeverLaunches(string json)
    {
        using var fixture = new Fixture(); fixture.Store.Write(new());
        File.WriteAllText(fixture.Store.StatePath, json);
        using var source = fixture.Create((report, token) => throw new InvalidOperationException("must not launch"));
        Assert.Equal("CLI_COMPATIBILITY_STATE_UNAVAILABLE", (await source.RefreshAsync(default)).Reason);
    }

    [Fact]
    public void PrivateStateAndLegacyEvidenceArePreserved()
    {
        using var fixture = new Fixture(); fixture.Store.Write(new());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.Store.StatePath));
        string legacy = Path.Combine(fixture.Directory, "legacy"); File.WriteAllText(legacy, "2.1.296\n");
        var store = new ClaudeCompatibilityStore(Path.GetDirectoryName(fixture.Store.StatePath)!, legacy);
        Assert.Throws<IOException>(() => store.Read());
        Assert.Throws<IOException>(() => store.Release(false));
        store.Release(true); Assert.True(store.Read().LegacyReleased); Assert.True(File.Exists(legacy));
        File.WriteAllText(legacy, "different"); Assert.Throws<IOException>(() => store.Read());
    }

    [Fact]
    public void SymlinkAndPublicPermissionsAreRejected()
    {
        using var fixture = new Fixture(); fixture.Store.Write(new());
        File.SetUnixFileMode(fixture.Store.StatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Throws<IOException>(() => fixture.Store.Read());
        File.SetUnixFileMode(fixture.Store.StatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        string link = Path.Combine(fixture.Directory, "linked"); Directory.CreateSymbolicLink(link, Path.GetDirectoryName(fixture.Store.StatePath)!);
        Assert.Throws<IOException>(() => new ClaudeCompatibilityStore(link).Read());
    }

    [Theory]
    [InlineData("before-state-write")]
    [InlineData("after-state-replace")]
    [InlineData("after-state-sync")]
    public async Task FailedFinalizationRetainsDurablePendingAndNeverPublishes(string checkpoint)
    {
        using var fixture = new Fixture();
        using var source = fixture.Create((report, token) =>
        {
            fixture.Store.WriteCheckpoint = step => { if (step == checkpoint) throw new IOException("simulated storage failure"); };
            return Task.FromResult(Available());
        });
        Assert.Equal("CLI_PREVIOUS_ATTEMPT_INCOMPLETE", (await source.RefreshAsync(default)).Reason);
        var restarted = new ClaudeCompatibilityStore(Path.GetDirectoryName(fixture.Store.StatePath)!);
        Assert.NotNull(restarted.Read().Pending);
        fixture.Store.WriteCheckpoint = null;
        source.Recheck(true);
        Assert.Null(restarted.Read().Pending);
        Assert.Single(restarted.Read().Resolutions);
    }

    [Fact]
    public async Task PendingWriteFailurePreventsAllCliExecution()
    {
        using var fixture = new Fixture();
        fixture.Store.WriteCheckpoint = _ => throw new IOException("simulated write failure");
        using var source = fixture.Create((_, _) => throw new Xunit.Sdk.XunitException("must never execute"));
        Assert.Equal("CLI_COMPATIBILITY_STATE_UNAVAILABLE", (await source.RefreshAsync(default)).Reason);
        Assert.NotNull(fixture.Store.Read().Pending);
    }

    [Fact]
    public async Task CancellationWithUnknownCleanupRemainsStoppedAfterRestart()
    {
        using var fixture = new Fixture(); using var cancel = new CancellationTokenSource();
        using var source = fixture.Create((report, token) =>
        {
            report("claude-session-started"); cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(Available());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.RefreshAsync(cancel.Token));
        Assert.NotNull(fixture.Store.Read().Pending);
        using var restarted = fixture.Create((_, _) => throw new Xunit.Sdk.XunitException("must not execute"));
        Assert.Equal("CLI_PREVIOUS_ATTEMPT_INCOMPLETE", (await restarted.RefreshAsync(default)).Reason);
    }

    [Fact]
    public async Task BackoffUsesOneFiveFifteenThirtySixtyMinutesAndManualReset()
    {
        using var fixture = new Fixture(); var time = new Clock(); int calls = 0;
        using var source = new MacClaudeAutomaticSource(null, fixture.Locator, fixture.Store, "test", TimeSpan.FromSeconds(5),
            (_, _, _) => new Source(_ => { calls++; return Task.FromResult(Failed()); }), time);
        foreach (int minutes in new[] { 1, 5, 15, 30, 60, 60 })
        {
            await source.RefreshAsync(default);
            Assert.Equal(time.GetUtcNow().AddMinutes(minutes), source.NextAttemptAt);
            await source.RefreshAsync(default);
            time.Now = source.NextAttemptAt!.Value;
        }
        Assert.Equal(6, calls);
        source.ResetRetry(); await source.RefreshAsync(default);
        Assert.Equal(time.GetUtcNow().AddMinutes(1), source.NextAttemptAt);
    }

    [Fact]
    public async Task ProvenMigrationBlocksCopiesOfSameIdentityButAllowsDifferentOfficialCandidate()
    {
        using var fixture = new Fixture(); string legacy = Path.Combine(fixture.Directory, "legacy"); File.WriteAllText(legacy, "2.1.296\n");
        var store = new ClaudeCompatibilityStore(Path.GetDirectoryName(fixture.Store.StatePath)!, legacy);
        Assert.Throws<IOException>(() => store.ImportLegacyMapping("2.1.999", IdentityA, new string('a', 64)));
        store.ImportLegacyMapping("2.1.296", IdentityA, new string('a', 64));
        Assert.Equal(MacClaudeBinaryIdentity.Parse(IdentityA).Key, store.Read().LegacyBinaryId);
        using var source = new MacClaudeAutomaticSource("/arbitrary/copy/claude", fixture.Locator, store, "changed-contract", TimeSpan.FromSeconds(5),
            (_, _, _) => new Source(_ => Task.FromResult(Available())));
        Assert.Equal("CLI_GROUP_ESCAPE_DETECTED", (await source.RefreshAsync(default)).Reason);
        fixture.Locator.Identity = IdentityB;
        Assert.Equal(UsageAvailability.Available, (await source.RefreshAsync(default)).Availability);
        store.Release(true); Assert.True(store.Read().LegacyReleased);
        Assert.NotNull(store.Read().LegacyEvidenceDigest); Assert.True(File.Exists(legacy));
    }

    [Fact]
    public async Task SameCandidateIdentityMismatchStopsButCleanNewCandidateRaceDoesNot()
    {
        using var fixture = new Fixture();
        using var source = fixture.Create((report, token) => { report("claude-identity-mismatch"); return Task.FromResult(Failed()); });
        Assert.Equal("CLI_IDENTITY_MISMATCH", (await source.RefreshAsync(default)).Reason);
        Assert.Single(fixture.Store.Read().Blocks);
        source.Recheck(true);
        fixture.Locator.Identity = IdentityA;
        using var racing = fixture.Create((report, token) => { report("claude-identity-mismatch"); fixture.Locator.Identity = IdentityB; return Task.FromResult(Failed()); });
        Assert.Equal("CLI_CANDIDATE_CHANGED", (await racing.RefreshAsync(default)).Reason);
        Assert.All(fixture.Store.Read().Blocks, block => Assert.NotNull(block.ReleasedAt));
    }

    [Fact]
    public async Task UnconfirmedStartupRetainsGlobalPendingEvenWhenOfficialCandidateChanges()
    {
        using var fixture = new Fixture();
        using var source = fixture.Create((report, token) => { report("claude-launch-requested"); fixture.Locator.Identity = IdentityB; return Task.FromResult(Failed()); });
        Assert.Equal("CLI_PREVIOUS_ATTEMPT_INCOMPLETE", (await source.RefreshAsync(default)).Reason);
        Assert.NotNull(fixture.Store.Read().Pending); Assert.Empty(fixture.Store.Read().Blocks);
        Assert.Equal("CLI_PREVIOUS_ATTEMPT_INCOMPLETE", (await source.RefreshAsync(default)).Reason);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ClaudeUsageObservation Available() => new(ClaudeUsageSourceKind.CliScreen,
        UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with { Availability = UsageAvailability.Available });
    private static ClaudeUsageObservation Failed() => new(ClaudeUsageSourceKind.CliScreen,
        UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with { Availability = UsageAvailability.Unavailable, Reason = "READY_TIMEOUT" });
    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "aiusage-contract-" + Guid.NewGuid().ToString("N"));
        internal MutableLocator Locator { get; } = new();
        internal ClaudeCompatibilityStore Store { get; }
        internal Fixture() { System.IO.Directory.CreateDirectory(Directory); Store = new(Path.Combine(Directory, "state")); }
        internal MacClaudeAutomaticSource Create(Func<Action<string>, CancellationToken, Task<ClaudeUsageObservation>> run, string contract = "test") =>
            new(null, Locator, Store, contract, TimeSpan.FromSeconds(5), (_, report, _) => new Source(token => run(report, token)));
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
    private sealed class MutableLocator : IClaudeExecutableLocator
    {
        internal string Identity { get; set; } = IdentityA;
        internal int Calls { get; private set; }
        public ClaudeExecutableInfo Resolve(string? path) { Calls++; return new("/synthetic/claude", null, true, "Anthropic", null) { Identity = Identity }; }
    }
    private sealed class Source(Func<CancellationToken, Task<ClaudeUsageObservation>> run) : IClaudeUsageSource
    { public Task<ClaudeUsageObservation> RefreshAsync(CancellationToken token) => run(token); }
}
