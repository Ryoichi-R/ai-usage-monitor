namespace AiUsageMonitor.Claude.Acquisition;

public sealed record ClaudeActiveRefreshOutcome(
    bool WasInvoked,
    Guid InvocationId,
    DateTimeOffset RequestedAt,
    ClaudeUsageObservation? Observation,
    string? SkipReason)
{
    public static ClaudeActiveRefreshOutcome Skipped(
        DateTimeOffset requestedAt,
        string reason) =>
        new(false, Guid.Empty, requestedAt, null, reason);
}
