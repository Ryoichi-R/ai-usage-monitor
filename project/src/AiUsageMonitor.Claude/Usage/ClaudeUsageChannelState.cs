using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Usage;

public sealed record ClaudeUsageChannelState(
    UsageSnapshot Snapshot,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessfulAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailureReason);
