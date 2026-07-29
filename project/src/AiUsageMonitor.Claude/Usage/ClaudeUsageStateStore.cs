using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Usage;

public sealed class ClaudeUsageStateStore
{
    private readonly object _sync = new();
    private ClaudeUsageObservationMerger _active = new();
    private readonly ClaudeUsageObservationMerger _passive = new();
    private DateTimeOffset? _activeAttempt;
    private DateTimeOffset? _activeFailure;
    private string? _activeFailureReason;

    public bool HasAnyObservation
    {
        get { lock (_sync) return _active.HasObservation || _passive.HasObservation; }
    }

    public bool HasPassiveObservation
    {
        get { lock (_sync) return _passive.HasObservation; }
    }

    public void InvalidateActive()
    {
        lock (_sync)
        {
            _active = new();
            _activeAttempt = null;
            _activeFailure = null;
            _activeFailureReason = null;
        }
    }

    public UsageSnapshot CommitActive(ClaudeUsageObservation observation, DateTimeOffset attemptedAt)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_sync)
        {
            _activeAttempt = attemptedAt;
            if (observation.Availability == UsageAvailability.Available &&
                observation.Source == ClaudeUsageSourceKind.CliScreen)
            {
                _activeFailure = null;
                _activeFailureReason = null;
                return _active.Merge(observation.Snapshot);
            }

            _activeFailure = observation.ReceivedAt;
            _activeFailureReason = observation.Source == ClaudeUsageSourceKind.CliScreen
                ? observation.Reason ?? observation.Availability.ToString().ToUpperInvariant()
                : "ACTIVE_SOURCE_NOT_CLI_SCREEN";
            UsageSnapshot failure = observation.Snapshot with
            {
                Availability = observation.Availability == UsageAvailability.Available
                    ? UsageAvailability.Error
                    : observation.Availability,
                Reason = _activeFailureReason,
                Windows = observation.Availability == UsageAvailability.Available ? [] : observation.Snapshot.Windows,
            };
            return _active.Merge(failure);
        }
    }

    public UsageSnapshot CommitPassive(UsageSnapshot snapshot)
    {
        lock (_sync) return _passive.Merge(snapshot);
    }

    public (ClaudeUsageChannelState Active, ClaudeUsageChannelState Passive) Read(
        DateTimeOffset now,
        ClaudeUsageFreshnessPolicy policy)
    {
        lock (_sync)
        {
            UsageSnapshot active = _active.Current(now, policy.ActiveTtl);
            UsageSnapshot passive = _passive.Current(now, ClaudeUsageFreshnessPolicy.PassiveTtl);
            return (
                new(active, _activeAttempt, active.LastSuccessfulAt, _activeFailure, _activeFailureReason),
                new(passive, null, passive.LastSuccessfulAt, null, null));
        }
    }
}
