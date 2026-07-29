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
                    active.LastAttemptAt, active.LastFailureReason, passiveReceivedAt);
            return new(passive.Snapshot, null, false, "NO_FRESH_PASSIVE",
                passive.LastSuccessfulAt is null
                    ? ClaudeUsageFreshnessKind.None
                    : ClaudeUsageFreshnessKind.StatusLineReceipt,
                active.LastAttemptAt, active.LastFailureReason, passiveReceivedAt);
        }
        if (mode != ClaudeUsageAcquisitionMode.StatusLineOnly && activeFresh)
            return new(active.Snapshot, ClaudeUsageSourceKind.CliScreen, false, "PREFERRED_ACTIVE",
                ClaudeUsageFreshnessKind.Cli, active.LastAttemptAt, active.LastFailureReason,
                passiveReceivedAt);
        if (mode == ClaudeUsageAcquisitionMode.OfficialCliOnly || activeInFlight)
            return new(active.Snapshot, activeFresh ? ClaudeUsageSourceKind.CliScreen : null, false,
                activeInFlight ? "ACTIVE_IN_FLIGHT" : "ACTIVE_ONLY",
                active.LastSuccessfulAt is null ? ClaudeUsageFreshnessKind.None : ClaudeUsageFreshnessKind.Cli,
                active.LastAttemptAt, active.LastFailureReason, passiveReceivedAt);
        if (passiveFresh)
            return new(passive.Snapshot, ClaudeUsageSourceKind.StatusLinePassive,
                mode == ClaudeUsageAcquisitionMode.Automatic,
                mode == ClaudeUsageAcquisitionMode.Automatic ? "PASSIVE_REFERENCE_FALLBACK" : "PASSIVE_ONLY",
                ClaudeUsageFreshnessKind.StatusLineReceipt, active.LastAttemptAt,
                active.LastFailureReason, passiveReceivedAt);
        UsageSnapshot empty = mode == ClaudeUsageAcquisitionMode.StatusLineOnly
            ? passive.Snapshot
            : active.Snapshot;
        return new(empty, null, false, "NO_FRESH_OBSERVATION", ClaudeUsageFreshnessKind.None,
            active.LastAttemptAt, active.LastFailureReason, passiveReceivedAt);
    }
}
