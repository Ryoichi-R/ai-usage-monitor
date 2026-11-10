using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Usage;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Tests;

public sealed class ClaudeUsageAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);
    private static readonly ClaudeUsageFreshnessPolicy ActivePolicy = new(300);

    [Theory]
    [InlineData(60, 10)]
    [InlineData(300, 11)]
    [InlineData(900, 33)]
    public void ActiveTtlIncludesTwoJitteredIntervals(int seconds, int expectedMinutes)
    {
        var policy = new ClaudeUsageFreshnessPolicy(seconds);
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), policy.ActiveTtl);
    }

    [Fact]
    public void PassiveReceiptDoesNotReplaceFreshActive()
    {
        var store = new ClaudeUsageStateStore();
        store.CommitActive(
            new(ClaudeUsageSourceKind.CliScreen, Snapshot(10, Now)),
            Now,
            ActivePolicy);
        store.CommitPassive(Snapshot(80, Now.AddMinutes(1)));
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var states = store.Read(Now.AddMinutes(1), policy);

        ClaudeUsageSelection selected = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.Automatic,
            states.Active,
            states.Passive,
            Now.AddMinutes(1),
            policy);

        Assert.Equal(ClaudeUsageSourceKind.CliScreen, selected.Source);
        Assert.False(selected.IsReference);
        Assert.Equal(10, selected.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(Now, states.Active.Snapshot.LastSuccessfulAt);
    }

    [Fact]
    public void AutomaticUsesPassiveOnlyAsExplicitReferenceFallback()
    {
        var store = new ClaudeUsageStateStore();
        store.CommitPassive(Snapshot(40, Now));
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var states = store.Read(Now, policy);

        ClaudeUsageSelection selected = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.Automatic,
            states.Active,
            states.Passive,
            Now,
            policy);

        Assert.True(selected.IsReference);
        Assert.Equal(ClaudeUsageSourceKind.StatusLinePassive, selected.Source);
        Assert.Equal(ClaudeUsageFreshnessKind.StatusLineReceipt, selected.FreshnessKind);
    }

    [Fact]
    public void AvailableNonCliActiveOutcomeFailsClosed()
    {
        var store = new ClaudeUsageStateStore();
        store.CommitActive(
            new(ClaudeUsageSourceKind.StatusLineActive, Snapshot(25, Now)),
            Now,
            ActivePolicy);
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var states = store.Read(Now, policy);

        Assert.NotEqual(UsageAvailability.Available, states.Active.Snapshot.Availability);
        Assert.Equal("ACTIVE_SOURCE_NOT_CLI_SCREEN", states.Active.LastFailureReason);
    }

    [Fact]
    public void PassiveTtlIsFreshAtBoundaryAndStaleAfterBoundary()
    {
        var policy = new ClaudeUsageFreshnessPolicy(900);
        UsageSnapshot passive = Snapshot(20, Now);

        Assert.True(policy.IsFresh(passive, Now.AddMinutes(10), active: false));
        Assert.False(policy.IsFresh(
            passive,
            Now.AddMinutes(10).AddTicks(1),
            active: false));
    }

    [Theory]
    [InlineData(60, 10)]
    [InlineData(300, 11)]
    [InlineData(900, 33)]
    public void ActiveTtlIsFreshAtBoundaryAndStaleAfterBoundary(
        int intervalSeconds,
        int ttlMinutes)
    {
        var policy = new ClaudeUsageFreshnessPolicy(intervalSeconds);
        UsageSnapshot active = Snapshot(20, Now) with
        {
            Windows =
            [
                new("seven_day", "7D", "seven_day", 20, 10080, Now.AddDays(7), null),
            ],
        };

        Assert.True(policy.IsFresh(active, Now.AddMinutes(ttlMinutes), active: true));
        Assert.False(policy.IsFresh(
            active,
            Now.AddMinutes(ttlMinutes).AddTicks(1),
            active: true));
    }

    [Fact]
    public void ResetPassageOverridesBothChannelTtls()
    {
        var policy = new ClaudeUsageFreshnessPolicy(900);
        UsageSnapshot snapshot = Snapshot(20, Now) with
        {
            Windows =
            [
                new("five_hour", "5H", "five_hour", 20, 300, Now.AddMinutes(1), null),
            ],
        };

        Assert.False(policy.IsFresh(snapshot, Now.AddMinutes(1), active: true));
        Assert.False(policy.IsFresh(snapshot, Now.AddMinutes(1), active: false));
    }

    [Fact]
    public void OfficialCliOnlyIngestsButNeverSelectsPassive()
    {
        var store = new ClaudeUsageStateStore();
        store.CommitPassive(Snapshot(55, Now));
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var states = store.Read(Now, policy);

        ClaudeUsageSelection selected = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            states.Active,
            states.Passive,
            Now,
            policy);

        Assert.Null(selected.Source);
        Assert.Equal(55, states.Passive.Snapshot.Windows.Single().UsedPercent);
    }

    [Fact]
    public void ActiveRecoveryReplacesReferenceWithoutMutatingPassive()
    {
        var store = new ClaudeUsageStateStore();
        store.CommitPassive(Snapshot(60, Now));
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var initial = store.Read(Now, policy);
        Assert.True(ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.Automatic,
            initial.Active,
            initial.Passive,
            Now,
            policy).IsReference);

        store.CommitActive(
            new(ClaudeUsageSourceKind.CliScreen, Snapshot(15, Now.AddMinutes(1))),
            Now.AddMinutes(1),
            ActivePolicy);
        var recovered = store.Read(Now.AddMinutes(1), policy);
        ClaudeUsageSelection selected = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.Automatic,
            recovered.Active,
            recovered.Passive,
            Now.AddMinutes(1),
            policy);

        Assert.False(selected.IsReference);
        Assert.Equal(ClaudeUsageSourceKind.CliScreen, selected.Source);
        Assert.Equal(15, selected.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(60, recovered.Passive.Snapshot.Windows.Single().UsedPercent);
    }

    [Fact]
    public void ModeSelectionReusesChannelsWithoutReinsertingSnapshots()
    {
        var store = new ClaudeUsageStateStore();
        store.CommitActive(
            new(ClaudeUsageSourceKind.CliScreen, Snapshot(10, Now)),
            Now,
            ActivePolicy);
        store.CommitPassive(Snapshot(70, Now.AddMinutes(1)));
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var states = store.Read(Now.AddMinutes(1), policy);

        ClaudeUsageSelection automatic = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.Automatic,
            states.Active,
            states.Passive,
            Now.AddMinutes(1),
            policy);
        ClaudeUsageSelection statusLine = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.StatusLineOnly,
            states.Active,
            states.Passive,
            Now.AddMinutes(1),
            policy);

        Assert.Equal(10, automatic.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(70, statusLine.Snapshot.Windows.Single().UsedPercent);
        Assert.Equal(Now, states.Active.LastSuccessfulAt);
        Assert.Equal(Now.AddMinutes(1), states.Passive.LastSuccessfulAt);
    }

    [Fact]
    public void StatusLineOnlyReportsMissingPassiveReceiptWhenNeverObserved()
    {
        var store = new ClaudeUsageStateStore();
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var states = store.Read(Now, policy);

        ClaudeUsageSelection selected = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.StatusLineOnly,
            states.Active,
            states.Passive,
            Now,
            policy);

        Assert.Null(selected.Source);
        Assert.Equal("NO_FRESH_PASSIVE", selected.Reason);
        Assert.Equal(ClaudeUsageFreshnessKind.None, selected.FreshnessKind);
    }

    [Fact]
    public void StatusLineOnlyReportsStalePassiveReceipt()
    {
        var store = new ClaudeUsageStateStore();
        store.CommitPassive(Snapshot(40, Now.AddMinutes(-20)));
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var states = store.Read(Now, policy);

        ClaudeUsageSelection selected = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.StatusLineOnly,
            states.Active,
            states.Passive,
            Now,
            policy);

        Assert.Null(selected.Source);
        Assert.Equal("NO_FRESH_PASSIVE", selected.Reason);
        Assert.Equal(ClaudeUsageFreshnessKind.StatusLineReceipt, selected.FreshnessKind);
    }

    [Fact]
    public void AutomaticReportsNoFreshObservationWhenBothChannelsAreEmpty()
    {
        var store = new ClaudeUsageStateStore();
        var policy = new ClaudeUsageFreshnessPolicy(300);
        var states = store.Read(Now, policy);

        ClaudeUsageSelection selected = ClaudeUsageSourceSelector.Select(
            ClaudeUsageAcquisitionMode.Automatic,
            states.Active,
            states.Passive,
            Now,
            policy);

        Assert.Null(selected.Source);
        Assert.Equal("NO_FRESH_OBSERVATION", selected.Reason);
        Assert.Equal(ClaudeUsageFreshnessKind.None, selected.FreshnessKind);
    }

    [Fact]
    public void AcceptedCliObservationClearsFailureOnlyAfterMerge()
    {
        var store = new ClaudeUsageStateStore();
        var policy = new ClaudeUsageFreshnessPolicy(300);
        UsageSnapshot original = Snapshot(20, Now);
        store.CommitActive(new(ClaudeUsageSourceKind.CliScreen, original), Now, ActivePolicy);
        DateTimeOffset retry = Now.AddMinutes(1);
        UsageSnapshot incoming = Snapshot(40, retry) with
        {
            Windows = [original.Windows[0] with { UsedPercent = 40, ResetsAt = Now.AddMinutes(20) }],
        };

        store.CommitActive(new(ClaudeUsageSourceKind.CliScreen, incoming), retry, ActivePolicy, out string? rejected);
        Assert.Equal("USAGE_RESET_REGRESSION_REJECTED", rejected);
        var failed = store.Read(Now.AddHours(2), policy).Active;
        Assert.Equal(rejected, failed.Snapshot.Reason);
        Assert.Equal(rejected, failed.LastFailureReason);
        Assert.Equal(Now, failed.LastSuccessfulAt);
        Assert.Equal(retry, failed.LastFailureAt);
        Assert.Equal(20d, failed.Snapshot.Windows.Single().UsedPercent);

        store.CommitActive(new(ClaudeUsageSourceKind.CliScreen, Snapshot(50, Now.AddMinutes(-1))), retry, ActivePolicy, out string? ignored);
        Assert.Null(ignored);
        Assert.Equal(rejected, store.Read(retry, policy).Active.LastFailureReason);

        DateTimeOffset recoveredAt = Now.AddMinutes(2);
        store.CommitActive(new(ClaudeUsageSourceKind.CliScreen, Snapshot(60, recoveredAt)), recoveredAt, ActivePolicy, out string? recovered);
        Assert.Equal("AVAILABLE", recovered);
        var active = store.Read(recoveredAt, policy).Active;
        Assert.Null(active.LastFailureReason);
        Assert.Equal(recoveredAt, active.LastSuccessfulAt);
    }

    [Fact]
    public void CliAndPassiveKeepTheirOwnStaleReasonAndAuthority()
    {
        var store = new ClaudeUsageStateStore();
        var policy = new ClaudeUsageFreshnessPolicy(300);
        UsageSnapshot initial = Snapshot(20, Now);
        initial = initial with { Windows = [initial.Windows[0] with { ResetsAt = Now.AddMinutes(3) }] };
        store.CommitActive(new(ClaudeUsageSourceKind.CliScreen, initial), Now, ActivePolicy);
        store.CommitPassive(initial);
        var rollover = store.Read(Now.AddMinutes(4), policy);
        Assert.Equal("RESET_PASSED", rollover.Active.Snapshot.Reason);
        Assert.Equal("RECEIVE_TIMEOUT", store.Read(Now.AddMinutes(12), policy).Active.Snapshot.Reason);

        DateTimeOffset retry = Now.AddMinutes(2);
        UsageSnapshot failure = UsageSnapshot.Loading(UsageProvider.Claude, retry) with
        {
            Availability = UsageAvailability.Error,
            Reason = ClaudeUsageFailure.ScreenParseFailed,
        };
        store.CommitActive(new(ClaudeUsageSourceKind.CliScreen, failure), retry, ActivePolicy);
        store.CommitPassive(failure);
        var stale = store.Read(Now.AddMinutes(12), policy);
        Assert.Equal(ClaudeUsageFailure.ScreenParseFailed, stale.Active.Snapshot.Reason);
        Assert.Equal("RESET_PASSED", stale.Passive.Snapshot.Reason);

        DateTimeOffset latest = Now.AddMinutes(13);
        store.CommitPassive(Snapshot(30, latest));
        var states = store.Read(latest, policy);
        var automatic = ClaudeUsageSourceSelector.Select(ClaudeUsageAcquisitionMode.Automatic,
            states.Active, states.Passive, latest, policy);
        Assert.True(automatic.IsReference);
        Assert.False(automatic.UsesCliSnapshot);
        var passiveOnly = ClaudeUsageSourceSelector.Select(ClaudeUsageAcquisitionMode.StatusLineOnly,
            states.Active, states.Passive, latest, policy);
        Assert.Null(passiveOnly.ActiveFailureReason);
        Assert.Null(passiveOnly.ActiveLastAttemptAt);
    }

    [Fact]
    public void CommitActiveReturnsTheSameCliSnapshotAsRead()
    {
        // 900秒間隔ではActiveTtlが33分になり、汎用の10分TTLとは判定が分かれる。
        var policy = new ClaudeUsageFreshnessPolicy(900);
        var store = new ClaudeUsageStateStore();
        UsageSnapshot initial = Snapshot(20, Now);
        initial = initial with { Windows = [initial.Windows[0] with { ResetsAt = Now.AddMinutes(20) }] };
        AssertMatchesRead(store.CommitActive(new(ClaudeUsageSourceKind.CliScreen, initial), Now, policy), Now);

        // 汎用TTL（10分）は超えるがActiveTtl内の失敗: CLI規則では最後の値と失敗理由を保つ。
        DateTimeOffset failedAt = Now.AddMinutes(15);
        UsageSnapshot failed = store.CommitActive(
            new(ClaudeUsageSourceKind.CliScreen, Failure(ClaudeUsageFailure.ScreenParseFailed, failedAt)),
            failedAt,
            policy);
        AssertMatchesRead(failed, failedAt);
        Assert.Equal(UsageAvailability.Available, failed.Availability);
        Assert.False(failed.IsStale);
        Assert.Equal(ClaudeUsageFailure.ScreenParseFailed, failed.Reason);

        // reset通過後の失敗: 汎用規則のRESET_PASSEDではなく、CLI規則で失敗理由を保つ。
        DateTimeOffset afterReset = Now.AddMinutes(25);
        UsageSnapshot rolled = store.CommitActive(
            new(ClaudeUsageSourceKind.CliScreen, Failure(ClaudeUsageFailure.SectionAmbiguous, afterReset)),
            afterReset,
            policy);
        AssertMatchesRead(rolled, afterReset);
        Assert.True(rolled.IsStale);
        Assert.Equal(ClaudeUsageFailure.SectionAmbiguous, rolled.Reason);

        void AssertMatchesRead(UsageSnapshot committed, DateTimeOffset at)
        {
            UsageSnapshot read = store.Read(at, policy).Active.Snapshot;
            Assert.Equal(read.Availability, committed.Availability);
            Assert.Equal(read.Reason, committed.Reason);
            Assert.Equal(read.IsStale, committed.IsStale);
            Assert.Equal(read.ReceivedAt, committed.ReceivedAt);
            Assert.Equal(read.LastSuccessfulAt, committed.LastSuccessfulAt);
            Assert.Equal(
                read.Windows.Select(window => (window.UsedPercent, window.ResetsAt)),
                committed.Windows.Select(window => (window.UsedPercent, window.ResetsAt)));
        }
    }

    [Fact]
    public void CommitActiveReturnsRejectedObservationAsRead()
    {
        var policy = new ClaudeUsageFreshnessPolicy(900);
        var store = new ClaudeUsageStateStore();
        UsageSnapshot original = Snapshot(20, Now);
        store.CommitActive(new(ClaudeUsageSourceKind.CliScreen, original), Now, policy);
        DateTimeOffset retry = Now.AddMinutes(15);
        UsageSnapshot regressed = Snapshot(40, retry) with
        {
            Windows = [original.Windows[0] with { UsedPercent = 40, ResetsAt = Now.AddMinutes(20) }],
        };

        UsageSnapshot committed = store.CommitActive(
            new(ClaudeUsageSourceKind.CliScreen, regressed),
            retry,
            policy,
            out string? rejected);

        UsageSnapshot read = store.Read(retry, policy).Active.Snapshot;
        Assert.Equal("USAGE_RESET_REGRESSION_REJECTED", rejected);
        Assert.Equal(read.Availability, committed.Availability);
        Assert.Equal(read.Reason, committed.Reason);
        Assert.Equal(read.IsStale, committed.IsStale);
        Assert.Equal(UsageAvailability.Available, committed.Availability);
        Assert.Equal(rejected, committed.Reason);
        Assert.Equal(20d, committed.Windows.Single().UsedPercent);
    }

    private static UsageSnapshot Failure(string reason, DateTimeOffset at) =>
        UsageSnapshot.Loading(UsageProvider.Claude, at) with
        {
            Availability = UsageAvailability.Error,
            Reason = reason,
        };

    private static UsageSnapshot Snapshot(double used, DateTimeOffset at) =>
        new(
            UsageProvider.Claude,
            at,
            at,
            UsageAvailability.Available,
            null,
            "Max",
            [new("five_hour", "5H", "five_hour", used, 300, at.AddHours(1), null)],
            null,
            false,
            at);
}
