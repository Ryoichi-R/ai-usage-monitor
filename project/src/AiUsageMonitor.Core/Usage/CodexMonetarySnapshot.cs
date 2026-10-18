namespace AiUsageMonitor.Core.Usage;

public sealed record CodexCreditSnapshot(
    bool HasCredits,
    bool IsUnlimited,
    decimal? Balance);

public sealed record CodexIndividualLimitSnapshot(
    decimal Used,
    decimal Limit,
    int RemainingPercent,
    DateTimeOffset? ResetsAt)
{
    public bool IsLimitReached => Used >= Limit || RemainingPercent == 0;
}
