using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Usage;

public sealed record ClaudeUsageFreshnessPolicy
{
    public static readonly TimeSpan MinimumActiveTtl = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan PassiveTtl = TimeSpan.FromMinutes(10);

    public ClaudeUsageFreshnessPolicy(int refreshIntervalSeconds)
    {
        int normalized = Math.Clamp(refreshIntervalSeconds, 60, 900);
        ActiveTtl = TimeSpan.FromSeconds(Math.Max(
            MinimumActiveTtl.TotalSeconds,
            normalized * 2.2));
    }

    public TimeSpan ActiveTtl { get; }

    public bool IsFresh(UsageSnapshot snapshot, DateTimeOffset now, bool active)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Availability != UsageAvailability.Available || snapshot.IsStale)
            return false;
        if (snapshot.Windows.Any(window => window.ResetsAt is { } reset && reset <= now))
            return false;
        return now - snapshot.ReceivedAt <= (active ? ActiveTtl : PassiveTtl);
    }
}
