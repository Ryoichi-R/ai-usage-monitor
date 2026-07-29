namespace AiUsageMonitor.Core.Usage;

public enum UsageProvider { Codex, Claude }

public sealed record UsageSnapshot(
    UsageProvider Provider,
    DateTimeOffset TakenAt,
    DateTimeOffset ReceivedAt,
    UsageAvailability Availability,
    string? Reason,
    string? PlanType,
    IReadOnlyList<UsageWindowSnapshot> Windows,
    decimal? Credits,
    bool IsStale,
    DateTimeOffset? LastSuccessfulAt,
    string? ClientVersion = null,
    CodexCreditSnapshot? CreditSnapshot = null,
    CodexIndividualLimitSnapshot? IndividualLimit = null,
    UsageAvailability RateLimitAvailability = UsageAvailability.Available,
    string? RateLimitReason = null,
    UsageResetDiagnostic? ResetDiagnostic = null)
{
    public static UsageSnapshot Loading(UsageProvider provider, DateTimeOffset now) =>
        new(provider, now, now, UsageAvailability.Loading, null, null, [], null, false, null);
}
