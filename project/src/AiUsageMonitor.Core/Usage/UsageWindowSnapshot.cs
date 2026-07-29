namespace AiUsageMonitor.Core.Usage;

public sealed record UsageWindowSnapshot(
    string? LimitId, string? LimitName, string SourceSlot, double UsedPercent,
    int? WindowDurationMins, DateTimeOffset? ResetsAt, string? ReachedType)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
    public bool IsLimitReached => UsedPercent >= 100d;
}
