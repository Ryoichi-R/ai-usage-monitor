using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Codex.Runtime;

public sealed record CodexAccountSnapshot(
    string AccountId,
    string DisplayName,
    bool ShowInWidget,
    UsageSnapshot Snapshot);
