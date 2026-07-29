using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Tests;

public sealed class UsageFreshnessFormatterTests
{
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FreshSuccessUsesAcquiredAndNormal()
    {
        UsageFreshnessDisplay? display = UsageFreshnessFormatter.Format(
            Snapshot(UsageAvailability.Available, null, Now.AddMinutes(-5)),
            Now,
            Tokyo);
        Assert.Equal("取得 14:55", display?.Text);
        Assert.Equal(UsageSeverity.Normal, display?.Severity);
    }

    [Fact]
    public void FailureUsesLastSuccessfulAndDoesNotFallbackToReceivedAt()
    {
        UsageSnapshot snapshot = Snapshot(
            UsageAvailability.Error,
            "TIMEOUT",
            Now.AddDays(-1)) with
        {
            ReceivedAt = Now,
        };
        UsageFreshnessDisplay? display = UsageFreshnessFormatter.Format(snapshot, Now, Tokyo);
        Assert.Equal("最終取得 07/25 15:00", display?.Text);
        Assert.Equal(UsageSeverity.Warning, display?.Severity);

        Assert.Null(UsageFreshnessFormatter.Format(
            snapshot with { LastSuccessfulAt = null },
            Now,
            Tokyo));
    }

    [Fact]
    public void StaleUsesDanger()
    {
        UsageFreshnessDisplay? display = UsageFreshnessFormatter.Format(
            Snapshot(UsageAvailability.Stale, "RESET_PASSED", Now.AddMinutes(-20)) with { IsStale = true },
            Now,
            Tokyo);
        Assert.Equal(UsageSeverity.Danger, display?.Severity);
    }

    [Fact]
    public void FreshLegacySnapshotMayFallbackToReceivedAt()
    {
        UsageSnapshot snapshot = Snapshot(
            UsageAvailability.Available,
            null,
            Now.AddMinutes(-5)) with
        {
            ReceivedAt = Now.AddMinutes(-3),
            LastSuccessfulAt = null,
        };

        UsageFreshnessDisplay? display = UsageFreshnessFormatter.Format(
            snapshot,
            Now,
            Tokyo);

        Assert.Equal("取得 14:57", display?.Text);
        Assert.Equal(UsageSeverity.Normal, display?.Severity);
    }

    [Fact]
    public void AvailableSnapshotWithFailureReasonUsesLastAcquiredWarning()
    {
        UsageFreshnessDisplay? display = UsageFreshnessFormatter.Format(
            Snapshot(
                UsageAvailability.Available,
                "USAGE_SCREEN_PARSE_FAILED",
                Now.AddMinutes(-5)),
            Now,
            Tokyo);

        Assert.Equal("最終取得 14:55", display?.Text);
        Assert.Equal(UsageSeverity.Warning, display?.Severity);
    }

    [Fact]
    public void TokyoCalendarBoundaryControlsDatePrefix()
    {
        DateTimeOffset justAfterMidnight =
            new(2026, 7, 26, 15, 5, 0, TimeSpan.Zero);
        UsageSnapshot snapshot = Snapshot(
            UsageAvailability.Available,
            null,
            new(2026, 7, 26, 14, 55, 0, TimeSpan.Zero));

        UsageFreshnessDisplay? display = UsageFreshnessFormatter.Format(
            snapshot,
            justAfterMidnight,
            Tokyo);

        Assert.Equal("取得 07/26 23:55", display?.Text);
    }

    [Theory]
    [InlineData("CLI", "CLI最終", "CLI 14:55")]
    [InlineData("SL受信", "SL最終", "SL受信 14:55")]
    public void ProviderNeutralPrefixesCanDescribeClaudeReceiptKind(
        string freshPrefix,
        string retainedPrefix,
        string expected)
    {
        UsageFreshnessDisplay? display = UsageFreshnessFormatter.Format(
            Snapshot(UsageAvailability.Available, null, Now.AddMinutes(-5)),
            Now,
            Tokyo,
            freshPrefix,
            retainedPrefix);

        Assert.Equal(expected, display?.Text);
    }

    private static UsageSnapshot Snapshot(
        UsageAvailability availability,
        string? reason,
        DateTimeOffset successfulAt) =>
        new(
            UsageProvider.Claude,
            Now,
            Now,
            availability,
            reason,
            null,
            [],
            null,
            false,
            successfulAt);
}
