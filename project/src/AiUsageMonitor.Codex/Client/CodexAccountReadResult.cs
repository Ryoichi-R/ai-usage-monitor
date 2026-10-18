using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Codex.Client;

internal sealed record AccountIdentity(string? Email)
{
    public override string ToString() => "AccountIdentity { Email = [REDACTED] }";
}

internal sealed record CodexAccountReadResult(
    UsageSnapshot Snapshot,
    AccountIdentity Identity);
