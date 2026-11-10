using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Usage;

public static class ClaudeUsageSourceSelector
{
    public static ClaudeUsageSelection Select(
        ClaudeUsageAcquisitionMode mode,
        ClaudeUsageChannelState active,
        ClaudeUsageChannelState passive,
        DateTimeOffset now,
        ClaudeUsageFreshnessPolicy policy,
        bool activeInFlight = false)
    {
        bool activeFresh = policy.IsFresh(active.Snapshot, now, active: true);
        bool passiveFresh = policy.IsFresh(passive.Snapshot, now, active: false);
        DateTimeOffset? passiveReceivedAt = passive.LastSuccessfulAt;
        if (mode == ClaudeUsageAcquisitionMode.StatusLineOnly)
        {
            if (passiveFresh)
                return new(passive.Snapshot, ClaudeUsageSourceKind.StatusLinePassive, false,
                    "PASSIVE_ONLY", ClaudeUsageFreshnessKind.StatusLineReceipt,
                    null, null, passiveReceivedAt);
            return new(passive.Snapshot, null, false, "NO_FRESH_PASSIVE",
                passive.LastSuccessfulAt is null
                    ? ClaudeUsageFreshnessKind.None
                    : ClaudeUsageFreshnessKind.StatusLineReceipt,
                null, null, passiveReceivedAt);
        }
        if (activeFresh)
            return SelectCli(ClaudeUsageSourceKind.CliScreen, "PREFERRED_ACTIVE");
        if (mode == ClaudeUsageAcquisitionMode.OfficialCliOnly || activeInFlight)
            return SelectCli(null, activeInFlight ? "ACTIVE_IN_FLIGHT" : "ACTIVE_ONLY");
        if (passiveFresh)
            return new(passive.Snapshot, ClaudeUsageSourceKind.StatusLinePassive,
                mode == ClaudeUsageAcquisitionMode.Automatic,
                mode == ClaudeUsageAcquisitionMode.Automatic ? "PASSIVE_REFERENCE_FALLBACK" : "PASSIVE_ONLY",
                ClaudeUsageFreshnessKind.StatusLineReceipt, active.LastAttemptAt,
                active.LastFailureReason, passiveReceivedAt);
        return SelectCli(null, "NO_FRESH_OBSERVATION");

        ClaudeUsageSelection SelectCli(ClaudeUsageSourceKind? source, string reason)
        {
            UsageSnapshot snapshot = active.Snapshot;
            bool hasSuccessfulValue = active.LastSuccessfulAt is not null;
            return new(snapshot, source, false, reason,
                hasSuccessfulValue ? ClaudeUsageFreshnessKind.Cli : ClaudeUsageFreshnessKind.None,
                active.LastAttemptAt, active.LastFailureReason, passiveReceivedAt)
            {
                UsesCliSnapshot = true,
                CliObservationExpiresAt = hasSuccessfulValue ? snapshot.ReceivedAt + policy.ActiveTtl : null,
            };
        }
    }
}
