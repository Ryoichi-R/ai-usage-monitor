using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Usage;

/// <summary>
/// Claude観測を統合する。同一reset window内では観測時刻(<see cref="UsageSnapshot.TakenAt"/>)が
/// 新しい値を採用し、遅れて到着した古い観測だけを拒否する。
/// 期間途中の上限引上げ等で使用済み割合が低下し得るため、単純な最大値保持は行わない。
/// </summary>
public sealed class ClaudeUsageObservationMerger
{
    public static readonly TimeSpan ResetEqualityTolerance = UsageWindowPolicy.ResetPrecisionTolerance;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private readonly object _sync = new();
    private UsageSnapshot? _current;
    private int _missingCount;
    private DateTimeOffset? _firstMissingAt;

    public bool HasObservation
    {
        get
        {
            lock (_sync) return _current is not null;
        }
    }

    public UsageSnapshot Current(DateTimeOffset now) => Current(now, StaleAfter);

    public UsageSnapshot Current(DateTimeOffset now, TimeSpan staleAfter)
    {
        lock (_sync)
        {
            if (_current is null) return UsageSnapshot.Loading(UsageProvider.Claude, now) with { Availability = UsageAvailability.Setup, Reason = "CLAUDE_SETUP" };
            bool expired = _current.Windows.Any(window => window.ResetsAt is { } reset && reset <= now);
            bool old = now - _current.ReceivedAt > staleAfter;
            return expired || old ? _current with { Availability = UsageAvailability.Stale, Reason = expired ? "RESET_PASSED" : "RECEIVE_TIMEOUT", IsStale = true } : _current;
        }
    }

    public UsageSnapshot Merge(UsageSnapshot incoming)
    {
        if (incoming.Provider != UsageProvider.Claude) throw new ArgumentException("Claude snapshot required.", nameof(incoming));
        lock (_sync)
        {
            // 遅れて到着した古い観測は、availabilityにかかわらず新しい観測を上書きしない。
            if (_current is not null && incoming.TakenAt < _current.TakenAt) return _current;

            if (incoming.Availability != UsageAvailability.Available)
            {
                _missingCount++;
                _firstMissingAt ??= incoming.ReceivedAt;
                if (_current is null)
                {
                    UsageAvailability availability = _missingCount >= 3 &&
                        incoming.ReceivedAt - _firstMissingAt >= TimeSpan.FromMinutes(1)
                            ? UsageAvailability.Unavailable
                            : incoming.Availability;
                    _current = incoming with
                    {
                        Availability = availability,
                        LastSuccessfulAt = null,
                    };
                    return _current;
                }

                // Authentication/capability failures invalidate a previous value immediately.
                if (incoming.Availability is UsageAvailability.SignedOut or UsageAvailability.Setup)
                {
                    _current = incoming with
                    {
                        Windows = [],
                        IsStale = false,
                        LastSuccessfulAt = null,
                    };
                    return _current;
                }

                if (incoming.Availability is UsageAvailability.Unsupported)
                {
                    _current = incoming with
                    {
                        Windows = [],
                        IsStale = false,
                        LastSuccessfulAt = GetSuccessfulHistory(_current),
                    };
                    return _current;
                }

                DateTimeOffset? successfulHistory = GetSuccessfulHistory(_current);
                if (successfulHistory is null)
                {
                    _current = incoming with
                    {
                        Windows = [],
                        IsStale = false,
                        LastSuccessfulAt = null,
                    };
                    return _current;
                }

                // A failed refresh must not move the freshness clock. ReceivedAt and
                // LastSuccessfulAt remain those of the last successful observation.
                _current = _current with
                {
                    Availability = UsageAvailability.Available,
                    Reason = incoming.Reason ?? incoming.Availability.ToString().ToUpperInvariant(),
                    IsStale = false,
                    LastSuccessfulAt = successfulHistory,
                };
                return Current(incoming.ReceivedAt);
            }

            _missingCount = 0;
            _firstMissingAt = null;
            Dictionary<int, UsageWindowSnapshot> previous = (_current?.Windows ?? [])
                .ToDictionary(window => window.WindowDurationMins ?? 0);
            var merged = new Dictionary<int, UsageWindowSnapshot>();
            string? rejectionReason = null;
            foreach (UsageWindowSnapshot incomingWindow in incoming.Windows)
            {
                int key = incomingWindow.WindowDurationMins ?? 0;
                MergeWindowDecision decision = previous.TryGetValue(
                    key,
                    out UsageWindowSnapshot? current)
                    ? MergeWindow(
                        current,
                        incomingWindow,
                        _current?.TakenAt ?? incoming.TakenAt,
                        incoming.TakenAt)
                    : ValidateNewWindow(incomingWindow, incoming.TakenAt);
                if (decision.Kind is
                    MergeWindowDecisionKind.RetainedPrevious or
                    MergeWindowDecisionKind.RejectedInvalidIncoming)
                {
                    rejectionReason ??= decision.Reason;
                }
                merged[key] = decision.Window;
            }

            if (rejectionReason is not null)
            {
                if (_current is null)
                {
                    _current = incoming with
                    {
                        Availability = UsageAvailability.Error,
                        Reason = rejectionReason,
                        Windows = [],
                        IsStale = false,
                        LastSuccessfulAt = null,
                    };
                    return _current;
                }

                _current = _current with
                {
                    Availability = UsageAvailability.Available,
                    Reason = rejectionReason,
                    IsStale = false,
                };
                return Current(incoming.ReceivedAt);
            }

            _current = incoming with
            {
                Windows = merged.Values.OrderBy(window => window.WindowDurationMins).ToArray(),
                Reason = null,
                IsStale = false,
                LastSuccessfulAt = incoming.ReceivedAt,
            };
            return _current;
        }
    }

    private static MergeWindowDecision ValidateNewWindow(
        UsageWindowSnapshot incoming,
        DateTimeOffset observedAt)
    {
        if (incoming.ResetsAt is null ||
            UsageWindowPolicy.IsValidReset(
                incoming.ResetsAt.Value,
                observedAt,
                incoming.WindowDurationMins))
        {
            return new(
                incoming,
                MergeWindowDecisionKind.Accepted,
                null);
        }

        return new(
            incoming,
            MergeWindowDecisionKind.RejectedInvalidIncoming,
            "USAGE_RESET_OUT_OF_RANGE");
    }

    private static MergeWindowDecision MergeWindow(
        UsageWindowSnapshot current,
        UsageWindowSnapshot incoming,
        DateTimeOffset currentObservedAt,
        DateTimeOffset incomingObservedAt)
    {
        if (current.ResetsAt is { } currentReset && incoming.ResetsAt is { } incomingReset)
        {
            bool currentValidAtOrigin = UsageWindowPolicy.IsValidReset(
                currentReset,
                currentObservedAt,
                current.WindowDurationMins);
            bool currentExpired = currentReset <= incomingObservedAt;
            bool incomingValid = UsageWindowPolicy.IsValidReset(
                incomingReset,
                incomingObservedAt,
                incoming.WindowDurationMins);
            if (!incomingValid)
            {
                return new(
                    current,
                    MergeWindowDecisionKind.RejectedInvalidIncoming,
                    "USAGE_RESET_OUT_OF_RANGE");
            }
            if (!currentValidAtOrigin || currentExpired)
            {
                return new(
                    incoming,
                    MergeWindowDecisionKind.RecoveredFromInvalidCurrent,
                    null);
            }
            // 既知のwindowより前へ戻るresetは、古いwindowの観測とみなして拒否する。
            if (incomingReset < currentReset - ResetEqualityTolerance)
            {
                return new(
                    current,
                    MergeWindowDecisionKind.RetainedPrevious,
                    "USAGE_RESET_REGRESSION_REJECTED");
            }
            // 同一window（許容差内）およびrolloverはいずれも新しい観測を採用する。
            return new(incoming, MergeWindowDecisionKind.Accepted, null);
        }

        if (incoming.ResetsAt is { } reset &&
            !UsageWindowPolicy.IsValidReset(
                reset,
                incomingObservedAt,
                incoming.WindowDurationMins))
        {
            return new(
                current,
                MergeWindowDecisionKind.RejectedInvalidIncoming,
                "USAGE_RESET_OUT_OF_RANGE");
        }

        // 新しい観測にreset情報がない場合、まだ有効な既知resetだけを引き継ぐ。
        if (current.ResetsAt is { } knownReset &&
            incoming.ResetsAt is null &&
            knownReset > incomingObservedAt)
        {
            return new(
                incoming with { ResetsAt = knownReset },
                MergeWindowDecisionKind.Accepted,
                null);
        }

        return new(incoming, MergeWindowDecisionKind.Accepted, null);
    }

    private static DateTimeOffset? GetSuccessfulHistory(UsageSnapshot snapshot) =>
        snapshot.LastSuccessfulAt ??
        (snapshot.Availability == UsageAvailability.Available &&
         !snapshot.IsStale &&
         string.IsNullOrWhiteSpace(snapshot.Reason)
            ? snapshot.ReceivedAt
            : null);

    private enum MergeWindowDecisionKind
    {
        Accepted,
        RetainedPrevious,
        RecoveredFromInvalidCurrent,
        RejectedInvalidIncoming,
    }

    private readonly record struct MergeWindowDecision(
        UsageWindowSnapshot Window,
        MergeWindowDecisionKind Kind,
        string? Reason);
}
