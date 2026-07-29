using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Acquisition;

/// <summary>
/// 公式CLI active sourceのsingle-flightとperiodic failure backoffを管理する。
/// runtime所有stateへのmergeや表示sourceの選択は行わない。
/// </summary>
public sealed class ClaudeUsageSourceCoordinator
{
    public static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(1);

    private readonly IClaudeUsageSource _activeSource;
    private readonly object _sync = new();
    private Task<ClaudeActiveRefreshOutcome>? _inFlight;
    private DateTimeOffset _nextAutomaticAttemptAt = DateTimeOffset.MinValue;

    public ClaudeUsageSourceCoordinator(IClaudeUsageSource activeSource)
    {
        _activeSource = activeSource ?? throw new ArgumentNullException(nameof(activeSource));
    }

    public bool IsRefreshInFlight
    {
        get { lock (_sync) return _inFlight is { IsCompleted: false }; }
    }

    public Task<ClaudeActiveRefreshOutcome> RefreshAsync(
        ClaudeUsageAcquisitionMode mode,
        bool manual,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (mode == ClaudeUsageAcquisitionMode.StatusLineOnly)
            return Task.FromResult(ClaudeActiveRefreshOutcome.Skipped(
                now,
                "STATUSLINE_ONLY"));
        Task<ClaudeActiveRefreshOutcome> shared;
        lock (_sync)
        {
            if (!manual && now < _nextAutomaticAttemptAt)
                return Task.FromResult(ClaudeActiveRefreshOutcome.Skipped(
                    now,
                    "ACTIVE_BACKOFF"));
            if (_inFlight is null)
            {
                Task<ClaudeActiveRefreshOutcome> created = RefreshCoreAsync(now);
                _inFlight = created;
                shared = created;
                _ = created.ContinueWith(
                    _ => ClearInFlight(created),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else shared = _inFlight;
        }
        return shared.WaitAsync(cancellationToken);
    }

    private async Task<ClaudeActiveRefreshOutcome> RefreshCoreAsync(DateTimeOffset requestedAt)
    {
        Guid invocationId = Guid.NewGuid();
        try
        {
            ClaudeUsageObservation observation =
                await _activeSource.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                _nextAutomaticAttemptAt = observation.Availability == UsageAvailability.Available
                    ? DateTimeOffset.MinValue
                    : requestedAt + FailureBackoff;
            }
            return new(true, invocationId, requestedAt, observation, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            lock (_sync)
            {
                _nextAutomaticAttemptAt = requestedAt + FailureBackoff;
            }
            return new(
                true,
                invocationId,
                requestedAt,
                new(
                    ClaudeUsageSourceKind.CliScreen,
                    UsageSnapshot.Loading(UsageProvider.Claude, requestedAt) with
                    {
                        Availability = UsageAvailability.Error,
                        Reason = "ACTIVE_SOURCE_EXCEPTION",
                    }),
                null);
        }
    }

    private void ClearInFlight(Task<ClaudeActiveRefreshOutcome> completed)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_inFlight, completed))
                _inFlight = null;
        }
    }
}
