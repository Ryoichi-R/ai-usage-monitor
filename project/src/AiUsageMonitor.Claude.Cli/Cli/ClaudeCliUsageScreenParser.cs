using System.Globalization;
using System.Text.RegularExpressions;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Cli;

/// <summary>Claude CLIのaccessibility向け/usage画面から既知の2枠だけを厳格に抽出する。</summary>
public static partial class ClaudeCliUsageScreenParser
{
    private static readonly TimeZoneInfo TokyoTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    public static UsageSnapshot Parse(
        IReadOnlyList<string> lines,
        DateTimeOffset observedAt,
        string? version) =>
        Parse(lines, observedAt, version, TimeZoneInfo.Local);

    private static readonly string[] SessionAnchors = ["Current session", "現在のセッション"];

    public static UsageSnapshot Parse(
        IReadOnlyList<string> lines,
        DateTimeOffset observedAt,
        string? version,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(localTimeZone);
        if (lines.Count == 0 || lines.Count > ClaudeCliScreenStateMachine.MaximumLines)
            return Failure(observedAt, version);

        // bufferは古いframeから新しいframeの順で並ぶ。最後のCurrent session（各言語版）から
        // 末尾までのsuffixだけを最新candidateとして切り出し、それより前に残る古いframeの
        // anchorは一切見ない。以降のanchor一意性判定もこのcandidate内だけで行われるため、
        // 古いframeとの重複では失敗せず、candidate内の重複・欠落だけをfail-closedにする。
        IReadOnlyList<string> candidate = ExtractLatestCandidate(lines);

        SectionResult session = ParseSection(
            candidate,
            SessionAnchors,
            ["Current week (all models)", "今週（すべてのモデル）"],
            observedAt,
            UsageWindowPolicy.FiveHourDurationMinutes,
            localTimeZone);
        SectionResult week = ParseSection(
            candidate,
            ["Current week (all models)", "今週（すべてのモデル）"],
            ["What's contributing", "利用上限への影響", "Usage credits", "使用クレジット", "使用量クレジット", "Esc to cancel"],
            observedAt,
            UsageWindowPolicy.SevenDayDurationMinutes,
            localTimeZone);

        if (!session.Success || !week.Success)
        {
            UsageResetDiagnostic? diagnostic = session.ResetFailureReason is not null
                ? session.ResetDiagnostic
                : week.ResetDiagnostic;
            return Failure(
                observedAt,
                version,
                session.ResetFailureReason ?? week.ResetFailureReason ?? "USAGE_SCREEN_PARSE_FAILED",
                diagnostic);
        }

        return new(
            UsageProvider.Claude,
            observedAt,
            DateTimeOffset.UtcNow,
            UsageAvailability.Available,
            null,
            null,
            [
                new(null, null, "five_hour", session.UsedPercent, UsageWindowPolicy.FiveHourDurationMinutes, session.ResetsAt, null),
                new(null, null, "seven_day", week.UsedPercent, UsageWindowPolicy.SevenDayDurationMinutes, week.ResetsAt, null),
            ],
            null,
            false,
            observedAt,
            version);
    }

    private static SectionResult ParseSection(
        IReadOnlyList<string> lines,
        string[] anchors,
        string[] terminators,
        DateTimeOffset observedAt,
        int expectedDurationMinutes,
        TimeZoneInfo localTimeZone)
    {
        int start = FindUniqueAnchor(lines, anchors);
        if (start < 0) return default;
        int end = lines.Count;
        for (int index = start + 1; index < lines.Count; index++)
        {
            if (terminators.Any(anchor =>
                    lines[index].Contains(anchor, StringComparison.OrdinalIgnoreCase)))
            {
                end = index;
                break;
            }
        }

        var percentages = new List<double>();
        DateTimeOffset? reset = null;
        int resetCount = 0;
        string? resetFailureReason = null;
        UsageResetDiagnostic? resetDiagnostic = null;
        for (int index = start + 1; index < end; index++)
        {
            string line = lines[index].Trim();
            Match percentage = UsedPercentageRegex().Match(line);
            if (percentage.Success &&
                double.TryParse(
                    percentage.Groups["value"].Value,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                out double used) &&
                used is >= 0 and <= 100)
            {
                bool duplicateMatches =
                    !percentage.Groups["prefix"].Success ||
                    (double.TryParse(
                        percentage.Groups["prefix"].Value,
                        NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture,
                        out double prefix) &&
                     prefix == used);
                if (duplicateMatches) percentages.Add(used);
            }

            ResetParseResult resetResult = TryParseReset(
                line,
                observedAt,
                expectedDurationMinutes,
                localTimeZone);
            if (resetResult.Matched && resetResult.Success)
            {
                reset = resetResult.Reset;
                resetCount++;
            }
            else if (resetResult.Matched)
            {
                resetFailureReason ??= resetResult.Reason;
                resetDiagnostic ??= resetResult.Diagnostic;
            }
        }

        return percentages.Count == 1 && resetCount == 1
            ? new(true, percentages[0], reset, null, null)
            : new(false, 0, null, resetFailureReason, resetDiagnostic);
    }

    /// <summary>
    /// bufferの末尾から最後のCurrent session（各言語版）を探し、そこから末尾までのsuffixを返す。
    /// 見つからない場合は入力をそのまま返し、後続のFindUniqueAnchorで従来どおり失敗させる。
    /// </summary>
    private static IReadOnlyList<string> ExtractLatestCandidate(IReadOnlyList<string> lines)
    {
        int lastSessionIndex = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            if (SessionAnchors.Any(anchor =>
                    lines[index].Contains(anchor, StringComparison.OrdinalIgnoreCase)))
                lastSessionIndex = index;
        }
        if (lastSessionIndex < 0) return lines;

        int length = lines.Count - lastSessionIndex;
        var candidate = new string[length];
        for (int offset = 0; offset < length; offset++)
            candidate[offset] = lines[lastSessionIndex + offset];
        return candidate;
    }

    private static int FindUniqueAnchor(IReadOnlyList<string> lines, string[] anchors)
    {
        int found = -1;
        for (int index = 0; index < lines.Count; index++)
        {
            if (!anchors.Any(anchor =>
                    lines[index].Contains(anchor, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (found >= 0) return -1;
            found = index;
        }
        return found;
    }

    private static ResetParseResult TryParseReset(
        string line,
        DateTimeOffset observedAt,
        int expectedDurationMinutes,
        TimeZoneInfo localTimeZone)
    {
        Match relative = JapaneseRelativeResetRegex().Match(line);
        if (relative.Success)
        {
            string category = relative.Groups["minutesOnly"].Success
                ? "relative_minutes_only"
                : "relative_hours_minutes";
            if (!TryParseNonNegative(relative.Groups["hours"], out long hours) ||
                !TryParseNonNegative(
                    relative.Groups["minutes"].Success
                        ? relative.Groups["minutes"]
                        : relative.Groups["minutesOnly"],
                    out long minutes) ||
                hours > (long.MaxValue - minutes) / 60)
            {
                return DiagnosticFailure(
                    "USAGE_RESET_OUT_OF_RANGE",
                    category,
                    "none",
                    expectedDurationMinutes);
            }

            long totalMinutes = hours * 60 + minutes;
            if (totalMinutes >
                expectedDurationMinutes +
                (long)UsageWindowPolicy.ResetPrecisionTolerance.TotalMinutes)
            {
                return DiagnosticFailure(
                    "USAGE_RESET_OUT_OF_RANGE",
                    category,
                    "none",
                    expectedDurationMinutes,
                    totalMinutes);
            }

            DateTimeOffset candidate;
            try
            {
                candidate = observedAt.AddMinutes(totalMinutes);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DiagnosticFailure(
                    "USAGE_RESET_OUT_OF_RANGE",
                    category,
                    "none",
                    expectedDurationMinutes,
                    totalMinutes);
            }
            return Candidate(
                candidate,
                observedAt,
                expectedDurationMinutes,
                category,
                "none");
        }

        Match japaneseAbsolute = JapaneseAbsoluteResetRegex().Match(line);
        if (japaneseAbsolute.Success)
        {
            DateTime japaneseNow = TimeZoneInfo.ConvertTime(observedAt, TokyoTimeZone).DateTime;
            if (!TryParseInt(japaneseAbsolute.Groups["month"], out int month) ||
                !TryParseInt(japaneseAbsolute.Groups["day"], out int day) ||
                !TryParseInt(japaneseAbsolute.Groups["hour"], out int hour) ||
                !TryParseInt(japaneseAbsolute.Groups["minute"], out int minute))
            {
                return DiagnosticFailure(
                    "USAGE_RESET_FORMAT_UNSUPPORTED",
                    "japanese_absolute",
                    "none",
                    expectedDurationMinutes);
            }

            DateTime localCandidate;
            try
            {
                localCandidate = new DateTime(
                    japaneseNow.Year,
                    month,
                    day,
                    hour,
                    minute,
                    0,
                    DateTimeKind.Unspecified);
                if (localCandidate <= japaneseNow)
                    localCandidate = localCandidate.AddYears(1);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DiagnosticFailure(
                    "USAGE_RESET_FORMAT_UNSUPPORTED",
                    "japanese_absolute",
                    "none",
                    expectedDurationMinutes);
            }

            DateTimeOffset candidate = TimeZoneInfo.ConvertTimeToUtc(localCandidate, TokyoTimeZone);
            return Candidate(
                candidate,
                observedAt,
                expectedDurationMinutes,
                "japanese_absolute",
                "none");
        }

        if (!line.StartsWith("Resets ", StringComparison.OrdinalIgnoreCase))
            return default;
        string remainder = line["Resets ".Length..].Trim();
        string zoneCategory = "none";
        Match zoneMatch = ZoneRegex().Match(remainder);
        if (zoneMatch.Success)
        {
            string zoneName = zoneMatch.Groups["zone"].Value.Trim();
            zoneCategory = zoneName.Equals("Asia/Tokyo", StringComparison.OrdinalIgnoreCase)
                ? "asia_tokyo"
                : zoneName.Equals("Tokyo Standard Time", StringComparison.OrdinalIgnoreCase)
                    ? "tokyo_standard_time"
                    : "other";
            if (zoneCategory == "other")
            {
                return DiagnosticFailure(
                    "USAGE_RESET_TIME_ZONE_UNSUPPORTED",
                    "unknown",
                    zoneCategory,
                    expectedDurationMinutes);
            }
            remainder = remainder[..zoneMatch.Index].Trim();
        }
        else if (remainder.Contains('(') || remainder.Contains(')'))
        {
            return DiagnosticFailure(
                "USAGE_RESET_TIME_ZONE_UNSUPPORTED",
                "unknown",
                "malformed",
                expectedDurationMinutes);
        }

        if (remainder.StartsWith("in ", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticFailure(
                "USAGE_RESET_FORMAT_UNSUPPORTED",
                "english_relative",
                zoneCategory,
                expectedDurationMinutes);
        }
        if (remainder.StartsWith("tomorrow ", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticFailure(
                "USAGE_RESET_FORMAT_UNSUPPORTED",
                "english_tomorrow",
                zoneCategory,
                expectedDurationMinutes);
        }
        if (EnglishTwentyFourHourRegex().IsMatch(remainder))
        {
            return DiagnosticFailure(
                "USAGE_RESET_FORMAT_UNSUPPORTED",
                "english_24_hour",
                zoneCategory,
                expectedDurationMinutes);
        }

        TimeZoneInfo zone = zoneCategory switch
        {
            "asia_tokyo" or "tokyo_standard_time" => TokyoTimeZone,
            _ => localTimeZone,
        };
        DateTime localNow = TimeZoneInfo.ConvertTime(observedAt, zone).DateTime;
        bool hasDate = remainder.Contains(',', StringComparison.Ordinal);
        string parseValue = hasDate ? $"{localNow.Year} {remainder}" : remainder;
        string[] formats = hasDate
            ? ["yyyy MMM d, htt", "yyyy MMM d, h:mmtt"]
            : ["htt", "h:mmtt"];
        if (!DateTime.TryParseExact(
                parseValue,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out DateTime parsed))
        {
            return DiagnosticFailure(
                "USAGE_RESET_FORMAT_UNSUPPORTED",
                hasDate ? "english_month_day" : "english_time_only",
                zoneCategory,
                expectedDurationMinutes);
        }

        DateTime local = hasDate
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified)
            : new DateTime(
                localNow.Year,
                localNow.Month,
                localNow.Day,
                parsed.Hour,
                parsed.Minute,
                0,
                DateTimeKind.Unspecified);
        if (local <= localNow)
        {
            if (!hasDate)
            {
                if (expectedDurationMinutes == UsageWindowPolicy.FiveHourDurationMinutes)
                {
                    DateTime nextDayLocal = local.AddDays(1);
                    DateTimeOffset nextDayReset =
                        TimeZoneInfo.ConvertTimeToUtc(nextDayLocal, zone);
                    if (UsageWindowPolicy.IsValidReset(
                            nextDayReset,
                            observedAt,
                            expectedDurationMinutes))
                    {
                        return Candidate(
                            nextDayReset,
                            observedAt,
                            expectedDurationMinutes,
                            "english_time_only_next_day",
                            zoneCategory);
                    }
                }
                return DiagnosticFailure(
                    "USAGE_RESET_AMBIGUOUS",
                    "english_time_only",
                    zoneCategory,
                    expectedDurationMinutes,
                    (long)Math.Truncate((local - localNow).TotalMinutes));
            }
            try
            {
                local = local.AddYears(1);
            }
            catch (ArgumentOutOfRangeException)
            {
                return DiagnosticFailure(
                    "USAGE_RESET_OUT_OF_RANGE",
                    "english_month_day",
                    zoneCategory,
                    expectedDurationMinutes);
            }
        }
        DateTimeOffset reset = TimeZoneInfo.ConvertTimeToUtc(local, zone);
        return Candidate(
            reset,
            observedAt,
            expectedDurationMinutes,
            hasDate ? "english_month_day" : "english_time_only",
            zoneCategory);
    }

    private static ResetParseResult Candidate(
        DateTimeOffset candidate,
        DateTimeOffset observedAt,
        int durationMinutes,
        string category,
        string zoneCategory)
    {
        long deltaMinutes = (long)Math.Truncate((candidate - observedAt).TotalMinutes);
        return
        UsageWindowPolicy.IsValidReset(candidate, observedAt, durationMinutes)
            ? new(true, true, candidate, null, null)
            : DiagnosticFailure(
                candidate <= observedAt
                    ? "USAGE_RESET_AMBIGUOUS"
                    : "USAGE_RESET_OUT_OF_RANGE",
                category,
                zoneCategory,
                durationMinutes,
                deltaMinutes);
    }

    private static ResetParseResult DiagnosticFailure(
        string reason,
        string category,
        string zoneCategory,
        int durationMinutes,
        long? deltaMinutes = null) =>
        new(
            true,
            false,
            default,
            reason,
            new(category, zoneCategory, deltaMinutes, durationMinutes));

    private static bool TryParseNonNegative(Group group, out long value)
    {
        if (!group.Success)
        {
            value = 0;
            return true;
        }
        return long.TryParse(
            group.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool TryParseInt(Group group, out int value) =>
        int.TryParse(
            group.Value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out value);

    private static UsageSnapshot Failure(
        DateTimeOffset now,
        string? version,
        string reason = "USAGE_SCREEN_PARSE_FAILED",
        UsageResetDiagnostic? diagnostic = null) =>
        new(
            UsageProvider.Claude,
            now,
            now,
            UsageAvailability.Error,
            reason,
            null,
            [],
            null,
            false,
            null,
            version,
            ResetDiagnostic: diagnostic);

    private readonly record struct SectionResult(
        bool Success,
        double UsedPercent,
        DateTimeOffset? ResetsAt,
        string? ResetFailureReason,
        UsageResetDiagnostic? ResetDiagnostic);

    private readonly record struct ResetParseResult(
        bool Matched,
        bool Success,
        DateTimeOffset Reset,
        string? Reason,
        UsageResetDiagnostic? Diagnostic);

    [GeneratedRegex(
        @"^(?:(?<prefix>\d{1,3}(?:\.\d+)?)%\s+)?(?<value>\d{1,3}(?:\.\d+)?)%\s*(?:used|使用済み)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsedPercentageRegex();

    [GeneratedRegex(
        @"^リセットまで\s*(?:(?<hours>\d+)時間(?:(?<minutes>\d+)分)?|(?<minutesOnly>\d+)分)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex JapaneseRelativeResetRegex();

    [GeneratedRegex(
        @"^(?<month>\d+)月(?<day>\d+)日\s*(?<hour>\d{1,2}):(?<minute>\d{2})\s*にリセット$",
        RegexOptions.CultureInvariant)]
    private static partial Regex JapaneseAbsoluteResetRegex();

    [GeneratedRegex(@"\s*\((?<zone>[^()]*)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneRegex();

    [GeneratedRegex(@"^\d{1,2}:\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnglishTwentyFourHourRegex();
}
