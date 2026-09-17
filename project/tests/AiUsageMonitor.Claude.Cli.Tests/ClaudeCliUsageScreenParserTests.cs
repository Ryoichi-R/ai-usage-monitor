using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Core.Usage;
using System.Globalization;

namespace AiUsageMonitor.Claude.Cli.Tests;

public sealed class ClaudeCliUsageScreenParserTests
{
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 24, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesKnownEnglishSectionsAndIgnoresPromotionAndActivityPercentages()
    {
        string[] lines =
        [
            "Usage: 1 input, 2 output",
            "Current session",
            "48% 48% used",
            "Resets 2pm (Asia/Tokyo)",
            "Current week (all models)",
            "32% 32% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "+50% weekly limits promo through Aug 28",
            "What's contributing to your limits usage?",
            "100% of your usage was at >200k context",
        ];

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.218");

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal([48d, 32d], result.Windows.Select(window => window.UsedPercent));
        Assert.Equal([52d, 68d], result.Windows.Select(window => window.RemainingPercent));
        Assert.All(result.Windows, window => Assert.NotNull(window.ResetsAt));
    }

    [Fact]
    public void ParsesKnownJapaneseSections()
    {
        string[] lines =
        [
            "使用状況",
            "現在のセッション",
            "48% 使用済み",
            "リセットまで 1時間30分",
            "今週（すべてのモデル）",
            "32% 使用済み",
            "7月30日 13:00 にリセット",
            "使用クレジット",
        ];

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.218");

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal([48d, 32d], result.Windows.Select(window => window.UsedPercent));
    }

    [Theory]
    [InlineData("101% used")]
    [InlineData("50% higher")]
    [InlineData("47% 48% used")]
    [InlineData("48% used", "48% used")]
    public void RejectsMissingAmbiguousOrOutOfRangeSections(params string[] sessionPercentages)
    {
        var lines = new List<string> { "Current session" };
        lines.AddRange(sessionPercentages);
        lines.Add("Resets 2pm (Asia/Tokyo)");
        lines.AddRange(
        [
            "Current week (all models)",
            "32% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "What's contributing to your limits usage?",
        ]);

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.218");

        Assert.Equal(UsageAvailability.Error, result.Availability);
        Assert.Equal("USAGE_SCREEN_PARSE_FAILED", result.Reason);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public void RejectsPastTimeWithoutRollingToTomorrow()
    {
        UsageSnapshot result = ParseWithSessionReset("Resets 12pm (Asia/Tokyo)");
        Assert.Equal(UsageAvailability.Error, result.Availability);
        Assert.Equal("USAGE_RESET_AMBIGUOUS", result.Reason);
    }

    [Fact]
    public void RejectsFutureTimeOutsideFiveHourHorizon()
    {
        UsageSnapshot result = ParseWithSessionReset("Resets 8pm (Asia/Tokyo)");
        Assert.Equal("USAGE_RESET_OUT_OF_RANGE", result.Reason);
    }

    [Fact]
    public void ParsesJapaneseMinutesOnlyReset()
    {
        UsageSnapshot result = ParseWithSessionReset("リセットまで45分");
        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal(ObservedAt.AddMinutes(45), result.Windows[0].ResetsAt);
    }

    [Theory]
    [InlineData("Resets in 45m", "USAGE_RESET_FORMAT_UNSUPPORTED")]
    [InlineData("Resets tomorrow at 2pm", "USAGE_RESET_FORMAT_UNSUPPORTED")]
    [InlineData("Resets 14:00", "USAGE_RESET_FORMAT_UNSUPPORTED")]
    [InlineData("Resets 2pm (America/Los_Angeles)", "USAGE_RESET_TIME_ZONE_UNSUPPORTED")]
    public void UnsupportedResetFormsFailClosed(string reset, string reason)
    {
        UsageSnapshot result = ParseWithSessionReset(reset);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void TimeOnlyResetAcceptsOnlyFutureTimeOnObservedTokyoDate()
    {
        DateTimeOffset before = TokyoAt(2026, 7, 26, 13, 59);
        UsageSnapshot accepted = ParseWithSessionReset(
            "Resets 2pm (Asia/Tokyo)",
            before);
        Assert.Equal(UsageAvailability.Available, accepted.Availability);
        Assert.Equal(TokyoAt(2026, 7, 26, 14, 0), accepted.Windows[0].ResetsAt);

        UsageSnapshot equal = ParseWithSessionReset(
            "Resets 2pm (Asia/Tokyo)",
            TokyoAt(2026, 7, 26, 14, 0));
        Assert.Equal("USAGE_RESET_AMBIGUOUS", equal.Reason);

        UsageSnapshot past = ParseWithSessionReset(
            "Resets 2pm (Asia/Tokyo)",
            TokyoAt(2026, 7, 26, 14, 40));
        Assert.Equal("USAGE_RESET_AMBIGUOUS", past.Reason);
    }

    [Fact]
    public void TimeOnlyResetAcceptsSameDayFutureWithinFiveHourHorizon()
    {
        DateTimeOffset observedAt = TokyoAt(2026, 7, 26, 14, 40);
        UsageSnapshot result = ParseWithSessionReset(
            "Resets 6:30pm (Asia/Tokyo)",
            observedAt);
        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal(TokyoAt(2026, 7, 26, 18, 30), result.Windows[0].ResetsAt);
    }

    [Fact]
    public void FiveHourTimeOnlyAcceptsNextDayOnlyWhenItFitsTheWindowHorizon()
    {
        DateTimeOffset lateEvening = TokyoAt(2026, 7, 26, 20, 8);
        UsageSnapshot accepted = ParseWithSessionReset(
            "Resets 12am (Asia/Tokyo)",
            lateEvening);
        Assert.Equal(UsageAvailability.Available, accepted.Availability);
        Assert.Equal(TokyoAt(2026, 7, 27, 0, 0), accepted.Windows[0].ResetsAt);

        UsageSnapshot tooFar = ParseWithSessionReset(
            "Resets 12am (Asia/Tokyo)",
            TokyoAt(2026, 7, 26, 18, 0));
        Assert.Equal("USAGE_RESET_AMBIGUOUS", tooFar.Reason);
    }

    [Fact]
    public void FiveHourHorizonUsesAsymmetricTwoMinuteTolerance()
    {
        DateTimeOffset observedAt = TokyoAt(2026, 7, 26, 14, 0);
        Assert.Equal(
            UsageAvailability.Available,
            ParseWithSessionReset(
                "Resets 7:02pm (Asia/Tokyo)",
                observedAt).Availability);
        Assert.Equal(
            "USAGE_RESET_OUT_OF_RANGE",
            ParseWithSessionReset(
                "Resets 7:03pm (Asia/Tokyo)",
                observedAt).Reason);
        Assert.Equal(
            "USAGE_RESET_AMBIGUOUS",
            ParseWithSessionReset(
                "Resets 1:59pm (Asia/Tokyo)",
                observedAt).Reason);
        Assert.Equal(
            "USAGE_RESET_AMBIGUOUS",
            ParseWithSessionReset(
                "Resets 1:58pm (Asia/Tokyo)",
                observedAt).Reason);
    }

    [Fact]
    public void SevenDayHorizonAcceptsUpperBoundaryAndRejectsBeyondIt()
    {
        DateTimeOffset observedAt = TokyoAt(2026, 7, 26, 14, 0);
        Assert.Equal(
            UsageAvailability.Available,
            ParseWithWeekReset(
                "Resets Aug 2, 2:02pm (Asia/Tokyo)",
                observedAt).Availability);
        Assert.Equal(
            "USAGE_RESET_OUT_OF_RANGE",
            ParseWithWeekReset(
                "Resets Aug 2, 2:03pm (Asia/Tokyo)",
                observedAt).Reason);
    }

    [Fact]
    public void MonthDayResetOnlyRollsYearWithinWindowHorizon()
    {
        UsageSnapshot pastMonthDay = ParseWithWeekReset(
            "Resets Jul 25, 1pm (Asia/Tokyo)",
            TokyoAt(2026, 7, 26, 14, 40));
        Assert.Equal("USAGE_RESET_OUT_OF_RANGE", pastMonthDay.Reason);

        UsageSnapshot yearBoundary = ParseWithWeekReset(
            "Resets Jan 2, 1am (Asia/Tokyo)",
            TokyoAt(2026, 12, 31, 23, 0));
        Assert.Equal(UsageAvailability.Available, yearBoundary.Availability);
        Assert.Equal(
            TokyoAt(2027, 1, 2, 1, 0),
            yearBoundary.Windows.Single(
                window => window.WindowDurationMins ==
                    UsageWindowPolicy.SevenDayDurationMinutes).ResetsAt);
    }

    [Fact]
    public void JapaneseAbsoluteUsesSameYearBoundaryAndHorizonRules()
    {
        UsageSnapshot past = ParseWithWeekReset(
            "7月25日 13:00 にリセット",
            TokyoAt(2026, 7, 26, 14, 40));
        Assert.Equal("USAGE_RESET_OUT_OF_RANGE", past.Reason);

        UsageSnapshot boundary = ParseWithWeekReset(
            "1月2日 01:00 にリセット",
            TokyoAt(2026, 12, 31, 23, 0));
        Assert.Equal(UsageAvailability.Available, boundary.Availability);
    }

    [Theory]
    [InlineData("リセットまで0分", "USAGE_RESET_AMBIGUOUS")]
    [InlineData("リセットまで5時間3分", "USAGE_RESET_OUT_OF_RANGE")]
    [InlineData("リセットまで999999999999999999999分", "USAGE_RESET_OUT_OF_RANGE")]
    public void JapaneseRelativeRejectsZeroOutOfHorizonAndOverflow(
        string reset,
        string reason)
    {
        UsageSnapshot result = ParseWithSessionReset(reset);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public void SevenDayTimeOnlyUsesSameFutureRule()
    {
        DateTimeOffset observedAt = TokyoAt(2026, 7, 26, 14, 0);
        Assert.Equal(
            UsageAvailability.Available,
            ParseWithWeekReset(
                "Resets 6pm (Asia/Tokyo)",
                observedAt).Availability);
        Assert.Equal(
            "USAGE_RESET_AMBIGUOUS",
            ParseWithWeekReset(
                "Resets 12pm (Asia/Tokyo)",
                observedAt).Reason);
    }

    [Fact]
    public void ZoneOmittedResetUsesInjectedLocalZoneOutsideTokyo()
    {
        TimeZoneInfo pacific =
            TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        DateTimeOffset observedAt = TimeZoneInfo.ConvertTime(
            new DateTimeOffset(2026, 7, 26, 20, 0, 0, TimeSpan.Zero),
            pacific);

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(
        [
            "Current session",
            "48% used",
            "Resets 2pm",
            "Current week (all models)",
            "32% used",
            "Resets Jul 29, 1pm",
            "What's contributing",
        ],
        observedAt,
        "2.1.218",
        pacific);

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 26, 21, 0, 0, TimeSpan.Zero),
            result.Windows[0].ResetsAt);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 29, 20, 0, 0, TimeSpan.Zero),
            result.Windows[1].ResetsAt);
    }

    [Theory]
    [InlineData("Resets 2pm")]
    [InlineData("Resets 2pm (Asia/Tokyo)")]
    [InlineData("Resets 2pm (Tokyo Standard Time)")]
    public void TokyoZoneOmittedOrExplicitIsAccepted(string reset)
    {
        UsageSnapshot result = ParseWithSessionReset(
            reset,
            TokyoAt(2026, 7, 26, 13, 0));
        Assert.Equal(UsageAvailability.Available, result.Availability);
    }

    [Fact]
    public void ResetFailureExposesOnlyTypedNonSensitiveDiagnosticContext()
    {
        UsageSnapshot result = ParseWithSessionReset(
            "Resets 12pm (Asia/Tokyo)",
            TokyoAt(2026, 7, 26, 13, 0));

        Assert.Equal("USAGE_RESET_AMBIGUOUS", result.Reason);
        UsageResetDiagnostic diagnostic = Assert.IsType<UsageResetDiagnostic>(
            result.ResetDiagnostic);
        Assert.Equal("english_time_only", diagnostic.Category);
        Assert.Equal("asia_tokyo", diagnostic.ZoneCategory);
        Assert.Equal(-60, diagnostic.CandidateDeltaMinutes);
        Assert.Equal(
            UsageWindowPolicy.FiveHourDurationMinutes,
            diagnostic.WindowDurationMinutes);
        Assert.DoesNotContain("Resets", diagnostic.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("12pm", diagnostic.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ResetDiagnosticDistinguishesRangeFormatAndZoneFailures()
    {
        UsageSnapshot range = ParseWithSessionReset(
            "Resets 8pm (Asia/Tokyo)",
            TokyoAt(2026, 7, 26, 13, 0));
        Assert.Equal("USAGE_RESET_OUT_OF_RANGE", range.Reason);
        Assert.Equal("english_time_only", range.ResetDiagnostic?.Category);
        Assert.Equal(420, range.ResetDiagnostic?.CandidateDeltaMinutes);

        UsageSnapshot format = ParseWithSessionReset(
            "Resets in 45m",
            TokyoAt(2026, 7, 26, 13, 0));
        Assert.Equal("english_relative", format.ResetDiagnostic?.Category);
        Assert.Null(format.ResetDiagnostic?.CandidateDeltaMinutes);

        UsageSnapshot zone = ParseWithSessionReset(
            "Resets 2pm (America/Los_Angeles)",
            TokyoAt(2026, 7, 26, 13, 0));
        Assert.Equal("other", zone.ResetDiagnostic?.ZoneCategory);
        Assert.DoesNotContain(
            "Los_Angeles",
            zone.ResetDiagnostic?.ToString() ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ParsesSuccessfullyAtTheHelperMaximumHeightBoundary()
    {
        // ClaudeConsoleHelper.MaximumHeight(120)ちょうどの行数でも正しくparseできることを
        // end-to-endで確認する（Parser自体のMaximumLines上限は400で別物）。
        var lines = new List<string>();
        for (int index = 0; index < 113; index++)
            lines.Add(FormattableString.Invariant($"  #. Contributing detail line {index}"));
        lines.AddRange(
        [
            "Current session",
            "48% used",
            "Resets 2pm (Asia/Tokyo)",
            "Current week (all models)",
            "32% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "What's contributing",
        ]);
        Assert.Equal(120, lines.Count);

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines.ToArray(), ObservedAt, "2.1.218");

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal([48d, 32d], result.Windows.Select(window => window.UsedPercent));
    }

    [Fact]
    public void LatestCompleteFrameIsUsedWhenAnOlderCompleteFrameSharesAnchors()
    {
        // screen bufferは古いframeから新しいframeの順に並ぶ。reset通過前の古いframeが
        // まだbuffer末尾に残っていても、最後のCurrent session以降のsuffixだけを見て、
        // 最新frameの数値を採用する（古いframeとのanchor重複では失敗しない）。
        string[] lines =
        [
            "Current session",
            "80% used",
            "Resets 1pm (Asia/Tokyo)",
            "Current week (all models)",
            "60% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "What's contributing",
            "Current session",
            "48% used",
            "Resets 2pm (Asia/Tokyo)",
            "Current week (all models)",
            "32% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "What's contributing",
        ];

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.218");

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal([48d, 32d], result.Windows.Select(window => window.UsedPercent));
    }

    [Fact]
    public void IncompleteLatestFrameFailsClosedInsteadOfFallingBackToOlderCompleteFrame()
    {
        // 最新frameが描画途中（percentage/resetが揃っていない）の場合、古い完全なframeへ
        // フォールバックせずfail-closedにする。取得側が再読込するまで古い値を騙って
        // 返さないため。
        string[] lines =
        [
            "Current session",
            "80% used",
            "Resets 1pm (Asia/Tokyo)",
            "Current week (all models)",
            "60% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "What's contributing",
            "Current session",
            "Current week (all models)",
        ];

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.218");

        Assert.Equal(UsageAvailability.Error, result.Availability);
        Assert.Equal("USAGE_SCREEN_PARSE_FAILED", result.Reason);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public void ExtraContributingLinesBeforeTheLatestFrameDoNotHideCurrentSessionAnchor()
    {
        // /usageのWhat's contributing配下はskill/MCP利用状況で行数が増減し、可視領域を
        // 圧迫しうる。console helperが直近120行のbufferを返す前提で、Current session
        // より前に大量のnoise行があってもparseできることを確認する。
        var lines = new List<string>();
        for (int index = 0; index < 96; index++)
            lines.Add(FormattableString.Invariant($"  #. Contributing detail line {index}"));
        lines.AddRange(
        [
            "Current session",
            "48% used",
            "Resets 2pm (Asia/Tokyo)",
            "Current week (all models)",
            "32% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "What's contributing",
        ]);
        Assert.True(lines.Count > 30 && lines.Count <= ClaudeConsoleHelperMaximumHeight);

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines.ToArray(), ObservedAt, "2.1.218");

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal([48d, 32d], result.Windows.Select(window => window.UsedPercent));
    }

    [Fact]
    public void DuplicateAnchorWithinLatestCandidateFailsClosed()
    {
        // candidate（最後のCurrent session以降のsuffix）内部で週間枠anchorが重複した場合は、
        // 古いframeとの重複とは異なり、fail-closedのままとする。
        string[] lines =
        [
            "Current session",
            "48% used",
            "Resets 2pm (Asia/Tokyo)",
            "Current week (all models)",
            "32% used",
            "Current week (all models)",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "What's contributing",
        ];

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.218");

        Assert.Equal(UsageAvailability.Error, result.Availability);
        Assert.Equal("USAGE_SCREEN_PARSE_FAILED", result.Reason);
    }

    [Fact]
    public void ZeroSessionWithoutResetKeepsUnknownTimestampAndParsesWeeklyWindow()
    {
        string[] lines =
        [
            "Current session",
            "0% 0% used",
            "Current week (all models)",
            "32% 32% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)",
            "Usage credits",
            "19% 19% used",
            "Esc to cancel",
        ];

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.274");

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal([0d, 32d], result.Windows.Select(window => window.UsedPercent));
        Assert.Null(result.Windows[0].ResetsAt);
        Assert.NotNull(result.Windows[1].ResetsAt);
    }

    [Theory]
    [InlineData("1% used", "")]
    [InlineData("0% used", "Resets tomorrow at 2pm")]
    [InlineData("0% used", "Loading usage...")]
    [InlineData("0% used", "0% used")]
    [InlineData("0% used", "1% 2% used")]
    [InlineData("0% used", "101% used")]
    [InlineData("", "")]
    public void MissingSessionResetDoesNotPermitNonzeroAmbiguousOrMalformedData(
        string percentage, string extraLine)
    {
        string[] lines =
        [
            "Current session", percentage, extraLine,
            "Current week (all models)", "32% used",
            "Resets Jul 30, 1pm (Asia/Tokyo)", "Esc to cancel",
        ];

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.274");

        Assert.Equal(UsageAvailability.Error, result.Availability);
        Assert.Empty(result.Windows);
    }

    [Fact]
    public void ZeroSessionDoesNotPermitMissingWeeklyReset()
    {
        string[] lines =
        [
            "Current session", "0% used",
            "Current week (all models)", "0% used", "Esc to cancel",
        ];

        UsageSnapshot result = ClaudeCliUsageScreenParser.Parse(lines, ObservedAt, "2.1.274");

        Assert.Equal(UsageAvailability.Error, result.Availability);
        Assert.Empty(result.Windows);
    }

    private const int ClaudeConsoleHelperMaximumHeight = 120;

    private static UsageSnapshot ParseWithSessionReset(
        string reset,
        DateTimeOffset? observedAt = null)
    {
        DateTimeOffset effectiveObservedAt = observedAt ?? ObservedAt;
        return ParseWithResets(
            reset,
            DefaultWeekReset(effectiveObservedAt),
            effectiveObservedAt);
    }

    private static UsageSnapshot ParseWithWeekReset(
        string reset,
        DateTimeOffset observedAt) =>
        ParseWithResets(
            "リセットまで1時間",
            reset,
            observedAt);

    private static UsageSnapshot ParseWithResets(
        string sessionReset,
        string weekReset,
        DateTimeOffset observedAt) =>
        ClaudeCliUsageScreenParser.Parse(
        [
            "Current session",
            "48% used",
            sessionReset,
            "Current week (all models)",
            "32% used",
            weekReset,
            "What's contributing",
        ],
        observedAt,
        "2.1.218",
        Tokyo);

    private static string DefaultWeekReset(DateTimeOffset observedAt)
    {
        DateTimeOffset local = TimeZoneInfo.ConvertTime(observedAt, Tokyo).AddDays(3);
        return $"Resets {local.ToString("MMM d, h:mmtt", CultureInfo.InvariantCulture)} (Asia/Tokyo)";
    }

    private static DateTimeOffset TokyoAt(
        int year,
        int month,
        int day,
        int hour,
        int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(9));
}
