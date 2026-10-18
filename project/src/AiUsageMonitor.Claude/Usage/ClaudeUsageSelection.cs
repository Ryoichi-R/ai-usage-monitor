using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Usage;

public enum ClaudeUsageFreshnessKind
{
    None,
    Cli,
    StatusLineReceipt,
}

public sealed record ClaudeUsageSelection(
    UsageSnapshot Snapshot,
    ClaudeUsageSourceKind? Source,
    bool IsReference,
    string Reason,
    ClaudeUsageFreshnessKind FreshnessKind,
    DateTimeOffset? ActiveLastAttemptAt,
    string? ActiveFailureReason,
    DateTimeOffset? PassiveLastReceivedAt);
