using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Usage;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Tests;

public sealed class ClaudeUsageAuthorityTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 3, 0, 0, TimeSpan.Zero);

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
            Now);
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
            Now);
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
            Now.AddMinutes(1));
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
            Now);
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
