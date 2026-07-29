using System.Globalization;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Presentation;

public enum UsageSeverity
{
    Accent = 0,
    Warning = 1,
    Danger = 2,
    Normal = 3,
}

public readonly record struct UsageFreshnessDisplay(string Text, UsageSeverity Severity);

public static class UsageFreshnessFormatter
{
    public static UsageFreshnessDisplay? Format(
        UsageSnapshot snapshot,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        string? freshPrefix = null,
        string? retainedPrefix = null)
    {
        DateTimeOffset? successfulAt = snapshot.LastSuccessfulAt;
        bool fresh = snapshot.Availability == UsageAvailability.Available &&
            !snapshot.IsStale &&
            string.IsNullOrWhiteSpace(snapshot.Reason);
        if (successfulAt is null && fresh)
            successfulAt = snapshot.ReceivedAt;
        if (successfulAt is null)
            return null;

        DateTimeOffset local = TimeZoneInfo.ConvertTime(successfulAt.Value, timeZone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        string prefix = fresh
            ? freshPrefix ?? "取得"
            : retainedPrefix ?? "最終取得";
        string value = local.Date == localNow.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM/dd HH:mm", CultureInfo.InvariantCulture);
        UsageSeverity severity = fresh
            ? UsageSeverity.Normal
            : snapshot.Availability == UsageAvailability.Stale || snapshot.IsStale
                ? UsageSeverity.Danger
                : UsageSeverity.Warning;
        return new($"{prefix} {value}", severity);
    }
}
