using System.Globalization;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Tests;

public sealed class UsageWindowFormatterTests
{
    [Theory]
    [InlineData(300, null, null, "five_hour", "5H")]
    [InlineData(10080, null, null, "seven_day", "7D")]
    [InlineData(15, null, null, "slot", "15M")]
    [InlineData(2880, null, null, "slot", "2D")]
    [InlineData(120, null, null, "slot", "2H")]
    [InlineData(61, null, null, "slot", "61M")]
    [InlineData(null, " weekly ", null, "slot", "WEEKLY")]
    [InlineData(null, null, " limit ", "slot", "LIMIT")]
    [InlineData(null, null, null, "fallback", "FALLBACK")]
    public void LabelUsesStableDurationAndFallbackRules(
        int? duration,
        string? limitName,
        string? limitId,
        string sourceSlot,
        string expected)
    {
        var window = new UsageWindowSnapshot(
            limitId,
            limitName,
            sourceSlot,
            25,
            duration,
            null,
            null);

        Assert.Equal(expected, UsageWindowFormatter.Label(window));
    }

    [Fact]
    public void RemainingFormatsWholeAndFractionalPercentages()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("75", UsageWindowFormatter.Remaining(Window(25)));
            Assert.Equal("74.5", UsageWindowFormatter.Remaining(Window(25.5)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ResetUsesMissingNearAndDistantFormats()
    {
        DateTimeOffset now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("RESET —", UsageWindowFormatter.Reset(Window(0), now));
        Assert.StartsWith("RESET ", UsageWindowFormatter.Reset(
            Window(0) with { ResetsAt = now.AddHours(2) },
            now));
        Assert.Contains("/", UsageWindowFormatter.Reset(
            Window(0) with { ResetsAt = now.AddDays(2) },
            now));
    }

    [Fact]
    public void ResetUsesCalendarDateInExplicitTokyoZone()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        DateTimeOffset now = new(2026, 7, 26, 14, 30, 0, TimeSpan.FromHours(9));
        Assert.Equal(
            "RESET 18:30",
            UsageWindowFormatter.Reset(
                Window(0) with { ResetsAt = now.AddHours(4) },
                now,
                tokyo));
        Assert.Equal(
            "RESET 07/27 00:00",
            UsageWindowFormatter.Reset(
                Window(0) with { ResetsAt = now.AddHours(9.5) },
                now,
                tokyo));
        Assert.Equal(
            "RESET 07/25 14:30",
            UsageWindowFormatter.Reset(
                Window(0) with { ResetsAt = now.AddDays(-1) },
                now,
                tokyo));
    }

    private static UsageWindowSnapshot Window(double used) =>
        new(null, null, "slot", used, 300, null, null);
}
