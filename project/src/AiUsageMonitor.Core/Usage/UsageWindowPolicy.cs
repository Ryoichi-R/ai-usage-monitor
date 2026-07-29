namespace AiUsageMonitor.Core.Usage;

public static class UsageWindowPolicy
{
    public const int FiveHourDurationMinutes = 300;
    public const int SevenDayDurationMinutes = 10080;
    public static readonly TimeSpan ResetPrecisionTolerance = TimeSpan.FromMinutes(2);

    public static bool TryGetDuration(int? durationMinutes, out TimeSpan duration)
    {
        duration = durationMinutes switch
        {
            FiveHourDurationMinutes => TimeSpan.FromMinutes(FiveHourDurationMinutes),
            SevenDayDurationMinutes => TimeSpan.FromMinutes(SevenDayDurationMinutes),
            _ => default,
        };
        return duration != default;
    }

    public static bool IsValidReset(
        DateTimeOffset candidate,
        DateTimeOffset observedAt,
        int? durationMinutes) =>
        TryGetDuration(durationMinutes, out TimeSpan duration) &&
        candidate > observedAt &&
        candidate <= observedAt + duration + ResetPrecisionTolerance;
}
