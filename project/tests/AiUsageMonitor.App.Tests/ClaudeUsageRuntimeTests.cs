using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.App.Tests;

public sealed class ClaudeUsageRuntimeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 25, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SameConfigurationReusesCoordinatorAndStatusLineOnlyDoesNotStartCli()
    {
        var source = new FixedSource(Snapshot(25, Now));
        int factoryCalls = 0;
        var runtime = new ClaudeUsageRuntime(_ =>
        {
            factoryCalls++;
            return source;
        });
        ClaudeActiveSourceConfiguration configuration = Configuration("claude-a");

        runtime.Configure(configuration, Now);
        await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            true,
            Now,
            CancellationToken.None);
        ClaudeRuntimeSnapshot configuredAgain = runtime.Configure(
            Configuration("CLAUDE-A"),
            Now.AddMinutes(1));
        await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.StatusLineOnly,
            false,
            Now.AddMinutes(1),
            CancellationToken.None);

        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, source.CallCount);
        Assert.Equal(UsageAvailability.Available, configuredAgain.Snapshot.Availability);
        Assert.Equal(25, configuredAgain.Snapshot.Windows.Single().UsedPercent);
    }

    [Fact]
    public void ReconfiguredCoordinatorExposesExistingObservationWithoutRefreshingFreshness()
    {
        int factoryCalls = 0;
        var runtime = new ClaudeUsageRuntime(_ =>
        {
            factoryCalls++;
            return new FixedSource(Snapshot(10, Now));
        });
        runtime.Configure(Configuration("claude-a"), Now);
        UsageSnapshot observed = Snapshot(35, Now);
        Assert.True(runtime.ObservePassive(observed));
        ClaudeRuntimeSnapshot before = runtime.Current(Now);

        ClaudeRuntimeSnapshot after = runtime.Configure(
            Configuration("claude-b"),
            Now.AddMinutes(1));

        Assert.Equal(2, factoryCalls);
        Assert.Equal(before.Snapshot.TakenAt, after.Snapshot.TakenAt);
        Assert.Equal(before.Snapshot.ReceivedAt, after.Snapshot.ReceivedAt);
        Assert.Equal(before.Snapshot.LastSuccessfulAt, after.Snapshot.LastSuccessfulAt);
        Assert.Equal(35, after.Snapshot.Windows.Single().UsedPercent);
    }

    [Fact]
    public void PassiveObservationPublishesExactlyOnceThroughCoordinator()
    {
        var runtime = new ClaudeUsageRuntime(_ =>
            new FixedSource(Snapshot(10, Now)));
        runtime.Configure(Configuration("claude-a"), Now);
        var published = new List<ClaudeRuntimeSnapshot>();
        runtime.SnapshotChanged += published.Add;

        Assert.True(runtime.ObservePassive(Snapshot(30, Now)));

        ClaudeRuntimeSnapshot item = Assert.Single(published);
        Assert.Equal(30, item.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(ClaudeUsageSourceKind.StatusLinePassive, runtime.CurrentSource);
    }

    [Fact]
    public async Task InitialCurrentDoesNotCountAsObservationButFirstErrorDoes()
    {
        var source = new ThrowingSource();
        var runtime = new ClaudeUsageRuntime(_ => source);
        runtime.Configure(Configuration("claude-a"), Now);

        ClaudeRuntimeSnapshot initial = runtime.Current(Now);
        UsageSnapshot setup = runtime.ForDisplay(initial.Snapshot, setupCompleted: false);
        UsageSnapshot waiting = runtime.ForDisplay(initial.Snapshot, setupCompleted: true);
        await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.StatusLineOnly,
            false,
            Now,
            CancellationToken.None);

        Assert.False(runtime.HasReceivedObservation);
        Assert.Equal(UsageAvailability.Setup, setup.Availability);
        Assert.Equal(UsageAvailability.Waiting, waiting.Availability);
        Assert.Equal(0, source.CallCount);

        UsageSnapshot error = await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            true,
            Now.AddSeconds(1),
            CancellationToken.None);

        Assert.True(runtime.HasReceivedObservation);
        Assert.Equal(1, source.CallCount);
        Assert.Equal(UsageAvailability.Error, error.Availability);
        Assert.Equal(
            UsageAvailability.Error,
            runtime.ForDisplay(error, setupCompleted: true).Availability);
    }

    [Fact]
    public async Task OldCoordinatorDelayedCompletionDoesNotRollBackUiOrSharedState()
    {
        var oldSource = new ControlledSource();
        var newSource = new FixedSource(Snapshot(20, Now.AddSeconds(10)));
        var runtime = new ClaudeUsageRuntime(configuration =>
            configuration.ExecutablePath.Equals("claude-a", StringComparison.OrdinalIgnoreCase)
                ? oldSource
                : newSource);
        runtime.Configure(Configuration("claude-a"), Now);
        var published = new List<ClaudeRuntimeSnapshot>();
        runtime.SnapshotChanged += published.Add;

        Task<UsageSnapshot> oldRefresh = runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            true,
            Now,
            CancellationToken.None);
        Assert.Equal(1, oldSource.CallCount);

        runtime.Configure(Configuration("claude-b"), Now.AddSeconds(5));
        await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            true,
            Now.AddSeconds(10),
            CancellationToken.None);
        oldSource.Complete(Snapshot(90, Now));
        await oldRefresh;

        ClaudeRuntimeSnapshot current = runtime.Current(Now.AddSeconds(11));
        ClaudeRuntimeSnapshot applied = Assert.Single(published);
        Assert.Equal(20, applied.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(20, current.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(Now.AddSeconds(10), current.Snapshot.TakenAt);
    }

    [Fact]
    public async Task OldCoordinatorDelayedErrorDoesNotInvalidateNewSuccess()
    {
        var oldSource = new ControlledSource();
        var newSource = new FixedSource(Snapshot(20, Now.AddSeconds(10)));
        var runtime = new ClaudeUsageRuntime(configuration =>
            configuration.ExecutablePath.Equals("claude-a", StringComparison.OrdinalIgnoreCase)
                ? oldSource
                : newSource);
        runtime.Configure(Configuration("claude-a"), Now);

        Task<UsageSnapshot> oldRefresh = runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            true,
            Now,
            CancellationToken.None);
        runtime.Configure(Configuration("claude-b"), Now.AddSeconds(5));
        await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            true,
            Now.AddSeconds(10),
            CancellationToken.None);
        oldSource.Complete(UsageSnapshot.Loading(UsageProvider.Claude, Now) with
        {
            Availability = UsageAvailability.Setup,
            Reason = "CLAUDE_TRUST_REQUIRED",
        });
        await oldRefresh;

        ClaudeRuntimeSnapshot current = runtime.Current(Now.AddSeconds(11));
        Assert.Equal(UsageAvailability.Available, current.Snapshot.Availability);
        Assert.Equal(20, current.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(Now.AddSeconds(10), current.Snapshot.TakenAt);
    }

    [Fact]
    public async Task AutomaticProductionPathInvokesCliWhenPassiveIsFresh()
    {
        var source = new FixedSource(Snapshot(12, Now.AddMinutes(1)));
        var runtime = new ClaudeUsageRuntime(_ => source);
        runtime.Configure(Configuration("claude-a"), Now);
        runtime.ObservePassive(Snapshot(80, Now));

        await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic,
            false,
            Now.AddMinutes(1),
            CancellationToken.None);
        ClaudeRuntimeSnapshot current = runtime.Current(Now.AddMinutes(1));

        Assert.Equal(1, source.CallCount);
        Assert.Equal(ClaudeUsageSourceKind.CliScreen, current.Selection.Source);
        Assert.Equal(12, current.Snapshot.Windows.Single().UsedPercent);
        Assert.False(current.Selection.IsReference);
    }

    [Fact]
    public async Task InitialActiveInFlightDoesNotPublishPassiveReference()
    {
        var source = new ControlledSource();
        var runtime = new ClaudeUsageRuntime(_ => source);
        runtime.Configure(Configuration("claude-a"), Now);
        var published = new List<ClaudeRuntimeSnapshot>();
        runtime.SnapshotChanged += published.Add;

        Task<UsageSnapshot> refresh = runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic,
            false,
            Now,
            CancellationToken.None);
        Assert.True(runtime.ObservePassive(Snapshot(75, Now.AddSeconds(1))));
        ClaudeRuntimeSnapshot healthOnly = Assert.Single(published);
        Assert.False(healthOnly.Selection.IsReference);
        Assert.Null(healthOnly.Selection.Source);
        Assert.Empty(healthOnly.Snapshot.Windows);

        source.Complete(Snapshot(15, Now.AddSeconds(2)));
        await refresh;

        ClaudeRuntimeSnapshot result = published[^1];
        Assert.Equal(2, published.Count);
        Assert.Equal(ClaudeUsageSourceKind.CliScreen, result.Selection.Source);
        Assert.Equal(15, result.Snapshot.Windows.Single().UsedPercent);
    }

    [Fact]
    public async Task OldGenerationInFlightCannotSuppressStatusLineOnlyPassiveSelection()
    {
        var oldSource = new ControlledSource();
        var runtime = new ClaudeUsageRuntime(configuration =>
            configuration.ExecutablePath.Equals("claude-a", StringComparison.OrdinalIgnoreCase)
                ? oldSource
                : new FixedSource(Snapshot(10, Now)));
        runtime.Configure(Configuration("claude-a"), Now);
        runtime.ObservePassive(Snapshot(40, Now));
        Task<UsageSnapshot> oldRefresh = runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic,
            false,
            Now,
            CancellationToken.None);

        runtime.Configure(Configuration("claude-b"), Now.AddSeconds(1));
        UsageSnapshot statusLine = await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.StatusLineOnly,
            false,
            Now.AddSeconds(1),
            CancellationToken.None);

        Assert.Equal(40, statusLine.Windows.Single().UsedPercent);
        Assert.Equal(ClaudeUsageSourceKind.StatusLinePassive, runtime.CurrentSource);

        oldSource.Complete(Snapshot(90, Now.AddSeconds(2)));
        await oldRefresh;
        Assert.Equal(40, runtime.Current(Now.AddSeconds(3)).Snapshot.Windows.Single().UsedPercent);
    }

    [Fact]
    public async Task PeriodicBackoffSkipDoesNotAdvanceActiveAttemptMetadata()
    {
        UsageSnapshot failure = UsageSnapshot.Loading(UsageProvider.Claude, Now) with
        {
            Availability = UsageAvailability.Error,
            Reason = "TIMEOUT",
        };
        var source = new FixedSource(failure);
        var runtime = new ClaudeUsageRuntime(_ => source);
        runtime.Configure(Configuration("claude-a"), Now);

        await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic,
            false,
            Now,
            CancellationToken.None);
        ClaudeRuntimeHealth first = runtime.CurrentHealth(Now);
        await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.Automatic,
            false,
            Now.AddSeconds(10),
            CancellationToken.None);
        ClaudeRuntimeHealth skipped = runtime.CurrentHealth(Now.AddSeconds(10));

        Assert.Equal(1, source.CallCount);
        Assert.Equal(Now, first.ActiveLastAttemptAt);
        Assert.Equal(first.ActiveLastAttemptAt, skipped.ActiveLastAttemptAt);
        Assert.Equal(first.ActiveFailureReason, skipped.ActiveFailureReason);
    }

    private static ClaudeActiveSourceConfiguration Configuration(string executable) =>
        new(executable, @"C:\app\claude-statusline-bridge.ps1", TimeSpan.FromSeconds(30));

    private static UsageSnapshot Snapshot(double used, DateTimeOffset observedAt) =>
        new(
            UsageProvider.Claude,
            observedAt,
            observedAt,
            UsageAvailability.Available,
            null,
            null,
            [new(null, null, "five_hour", used, 300, observedAt.AddHours(1), null)],
            null,
            false,
            observedAt,
            "test");

    private sealed class FixedSource(UsageSnapshot snapshot) : IClaudeUsageSource
    {
        public int CallCount { get; private set; }

        public Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new ClaudeUsageObservation(
                ClaudeUsageSourceKind.CliScreen,
                snapshot));
        }
    }

    private sealed class ThrowingSource : IClaudeUsageSource
    {
        public int CallCount { get; private set; }

        public Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            throw new InvalidOperationException("test");
        }
    }

    private sealed class ControlledSource : IClaudeUsageSource
    {
        private readonly TaskCompletionSource<ClaudeUsageObservation> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return _completion.Task.WaitAsync(cancellationToken);
        }

        public void Complete(UsageSnapshot snapshot) =>
            _completion.SetResult(new ClaudeUsageObservation(
                ClaudeUsageSourceKind.CliScreen,
                snapshot));
    }
}
