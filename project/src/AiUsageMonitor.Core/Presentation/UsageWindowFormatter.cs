using System.Globalization;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Presentation;

public static class UsageWindowFormatter
{
    public static string Label(UsageWindowSnapshot window) => window.WindowDurationMins switch
    {
        UsageWindowPolicy.FiveHourDurationMinutes => "5H",
        UsageWindowPolicy.SevenDayDurationMinutes => "7D",
        > 0 and < 60 => $"{window.WindowDurationMins}M",
        > 0 when window.WindowDurationMins % 1440 == 0 => $"{window.WindowDurationMins / 1440}D",
        > 0 when window.WindowDurationMins % 60 == 0 => $"{window.WindowDurationMins / 60}H",
        > 0 => $"{window.WindowDurationMins}M",
        _ when !string.IsNullOrWhiteSpace(window.LimitName) => window.LimitName!.Trim().ToUpperInvariant(),
        _ when !string.IsNullOrWhiteSpace(window.LimitId) => window.LimitId!.Trim().ToUpperInvariant(),
        _ => window.SourceSlot.ToUpperInvariant(),
    };

    public static string Remaining(UsageWindowSnapshot window) =>
        window.RemainingPercent.ToString(window.RemainingPercent % 1d == 0d ? "0" : "0.0", CultureInfo.CurrentCulture);

    public static string Reset(
        UsageWindowSnapshot window,
        DateTimeOffset now,
        TimeZoneInfo? timeZone = null)
    {
        if (window.ResetsAt is null) return "RESET —";
        timeZone ??= TimeZoneInfo.Local;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(window.ResetsAt.Value, timeZone);
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        return local.Date == localNow.Date
            ? $"RESET {local:t}"
            : $"RESET {local:MM/dd HH:mm}";
    }
}
