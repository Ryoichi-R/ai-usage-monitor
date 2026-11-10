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

    public UsageSnapshot CommitActive(
        ClaudeUsageObservation observation,
        DateTimeOffset attemptedAt,
        ClaudeUsageFreshnessPolicy policy) =>
        CommitActive(observation, attemptedAt, policy, out _);

    /// <summary>
    /// 能動取得の結果を記録し、<see cref="Read"/>を<paramref name="observation"/>の受信時刻で
    /// 呼んだ場合と同じactive側snapshot（CLI用のstale判定とActiveTtl）を返す。
    /// </summary>
    public UsageSnapshot CommitActive(
        ClaudeUsageObservation observation,
        DateTimeOffset attemptedAt,
        ClaudeUsageFreshnessPolicy policy,
        out string? diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(policy);
        lock (_sync)
        {
            bool isCli = observation.Source == ClaudeUsageSourceKind.CliScreen;
            bool available = observation.Availability == UsageAvailability.Available;
            string? failureReason = isCli && available ? null
                : isCli ? observation.Reason ?? observation.Availability.ToString().ToUpperInvariant()
                : "ACTIVE_SOURCE_NOT_CLI_SCREEN";
            UsageSnapshot incoming = failureReason is null
                ? observation.Snapshot
                : observation.Snapshot with
                {
                    Availability = available ? UsageAvailability.Error : observation.Availability,
                    Reason = failureReason,
                    Windows = available ? [] : observation.Snapshot.Windows,
                };
            // Merger.Mergeの戻り値は汎用（CLI用でない）stale判定なので使わず、表示と同じ規則で読み直す。
            _active.Merge(incoming, out string? rejectionReason, out bool ignored);
            diagnosticCode = null;
            if (!ignored)
            {
                _activeAttempt = attemptedAt;
                _activeFailureReason = rejectionReason ?? failureReason;
                _activeFailure = _activeFailureReason is null ? null : observation.ReceivedAt;
                diagnosticCode = _activeFailureReason ?? "AVAILABLE";
            }
            return _active.Current(observation.ReceivedAt, policy.ActiveTtl, cliScreen: true);
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
            UsageSnapshot active = _active.Current(now, policy.ActiveTtl, cliScreen: true);
            UsageSnapshot passive = _passive.Current(now, ClaudeUsageFreshnessPolicy.PassiveTtl);
            return (
                new(active, _activeAttempt, active.LastSuccessfulAt, _activeFailure, _activeFailureReason),
                new(passive, null, passive.LastSuccessfulAt, null, null));
        }
    }
}
