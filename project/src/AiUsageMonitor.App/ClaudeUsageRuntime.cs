using System.IO;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Usage;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.App;

internal sealed class ClaudeUsageRuntime
{
    private readonly Func<ClaudeActiveSourceConfiguration, IClaudeUsageSource> _sourceFactory;
    private readonly ClaudeUsageStateStore _state = new();
    private readonly object _sync = new();
    private ClaudeUsageSourceCoordinator? _coordinator;
    private ClaudeActiveSourceConfiguration? _configuration;
    private ClaudeUsageAcquisitionMode _mode = ClaudeUsageAcquisitionMode.Automatic;
    private ClaudeUsageFreshnessPolicy _policy = new(300);
    private Guid _lastCommittedInvocationId;
    private ClaudeUsageSourceKind? _currentSource;
    private DateTimeOffset? _statusLineOnlySince;
    private long _generation;

    public ClaudeUsageRuntime(Func<ClaudeActiveSourceConfiguration, IClaudeUsageSource> sourceFactory)
    {
        _sourceFactory = sourceFactory ?? throw new ArgumentNullException(nameof(sourceFactory));
    }

    public event Action<ClaudeRuntimeSnapshot>? SnapshotChanged;

    public bool HasReceivedObservation => _state.HasAnyObservation;

    public bool IsConfigured
    {
        get
        {
            lock (_sync) return _coordinator is not null;
        }
    }

    public ClaudeUsageSourceKind? CurrentSource
    {
        get
        {
            lock (_sync) return _currentSource;
        }
    }

    public ClaudeRuntimeSnapshot Configure(
        ClaudeActiveSourceConfiguration configuration,
        DateTimeOffset now,
        int refreshIntervalSeconds = 300)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_sync)
        {
            _policy = new(refreshIntervalSeconds);
            if (_coordinator is not null &&
                _configuration is not null &&
                _configuration.IsEquivalentTo(configuration))
            {
            }
            else
            {
                DetachCoordinator();
                _generation++;
                IClaudeUsageSource source = _sourceFactory(configuration);
                _coordinator = CreateCoordinator(source);
                _configuration = configuration;
                _lastCommittedInvocationId = Guid.Empty;
                _state.InvalidateActive();
            }
            return SnapshotLocked(now);
        }
    }

    public void Disable()
    {
        lock (_sync)
        {
            DetachCoordinator();
            _configuration = null;
            _lastCommittedInvocationId = Guid.Empty;
            _generation++;
        }
    }

    public ClaudeRuntimeSnapshot Current(DateTimeOffset now)
    {
        lock (_sync) return SnapshotLocked(now);
    }

    public ClaudeRuntimeHealth CurrentHealth(DateTimeOffset now)
    {
        lock (_sync)
        {
            ClaudeRuntimeSnapshot current = SnapshotLocked(now);
            return new(
                current.Selection.Source,
                current.Selection.ActiveLastAttemptAt,
                current.Selection.ActiveFailureReason,
                current.Selection.PassiveLastReceivedAt);
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_sync) return generation == _generation;
    }

    public UsageSnapshot ForDisplay(UsageSnapshot snapshot, bool setupCompleted)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!setupCompleted ||
            HasReceivedObservation ||
            snapshot.Availability != UsageAvailability.Setup)
        {
            return snapshot;
        }

        return snapshot with
        {
            Availability = UsageAvailability.Waiting,
            Reason = "CLAUDE_WAITING_FOR_FIRST_OBSERVATION",
            IsStale = false,
        };
    }

    public async Task<UsageSnapshot> RefreshAsync(
        ClaudeUsageAcquisitionMode mode,
        bool manual,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ClaudeUsageSourceCoordinator coordinator;
        long generation;
        Task<ClaudeActiveRefreshOutcome> activeTask;
        lock (_sync)
        {
            coordinator = _coordinator ??
                throw new InvalidOperationException("Claude usage coordinator is unavailable.");
            generation = _generation;
            if (mode == ClaudeUsageAcquisitionMode.StatusLineOnly &&
                _mode != ClaudeUsageAcquisitionMode.StatusLineOnly)
                _statusLineOnlySince = now;
            else if (mode != ClaudeUsageAcquisitionMode.StatusLineOnly)
                _statusLineOnlySince = null;
            _mode = mode;
            if (mode == ClaudeUsageAcquisitionMode.StatusLineOnly)
            {
                ClaudeRuntimeSnapshot passive = SnapshotLocked(now);
                Publish(passive);
                return passive.Snapshot;
            }
            activeTask = coordinator.RefreshAsync(mode, manual, now, cancellationToken);
        }

        ClaudeActiveRefreshOutcome outcome;
        outcome = await activeTask.ConfigureAwait(false);

        ClaudeRuntimeSnapshot? current = null;
        lock (_sync)
        {
            if (generation == _generation &&
                ReferenceEquals(coordinator, _coordinator) &&
                outcome.WasInvoked &&
                outcome.Observation is { } observation &&
                outcome.InvocationId != _lastCommittedInvocationId)
            {
                _state.CommitActive(observation, outcome.RequestedAt);
                _lastCommittedInvocationId = outcome.InvocationId;
                current = SnapshotLocked(observation.ReceivedAt);
            }
            else if (generation == _generation &&
                ReferenceEquals(coordinator, _coordinator))
            {
                current = SnapshotLocked(now);
            }
        }
        if (current is { } committed) Publish(committed);
        return current?.Snapshot ?? Current(DateTimeOffset.UtcNow).Snapshot;
    }

    public bool ObservePassive(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ClaudeRuntimeSnapshot? changed = null;
        lock (_sync)
        {
            if (_coordinator is null) return false;
            ClaudeRuntimeSnapshot before = SnapshotLocked(snapshot.ReceivedAt);
            _state.CommitPassive(snapshot);
            ClaudeRuntimeSnapshot after = SnapshotLocked(snapshot.ReceivedAt);
            if (before.Selection != after.Selection) changed = after;
        }
        if (changed is { } candidate) Publish(candidate);
        return true;
    }

    private static ClaudeUsageSourceCoordinator CreateCoordinator(IClaudeUsageSource source) =>
        new ClaudeUsageSourceCoordinator(source);

    private ClaudeRuntimeSnapshot SnapshotLocked(DateTimeOffset now)
    {
        (ClaudeUsageChannelState active, ClaudeUsageChannelState passive) = _state.Read(now, _policy);
        ClaudeUsageSelection selection = ClaudeUsageSourceSelector.Select(
            _mode,
            active,
            passive,
            now,
            _policy,
            _coordinator?.IsRefreshInFlight == true);
        if (_mode == ClaudeUsageAcquisitionMode.StatusLineOnly &&
            !_state.HasPassiveObservation &&
            _statusLineOnlySince is { } since)
        {
            int elapsedMinutes = Math.Max(0, (int)(now - since).TotalMinutes);
            selection = selection with
            {
                Snapshot = selection.Snapshot with
                {
                    Availability = UsageAvailability.Waiting,
                    Reason = elapsedMinutes < 2
                        ? "STATUSLINE_WAITING"
                        : $"STATUSLINE_NOT_RECEIVED:{elapsedMinutes}",
                    Windows = [],
                    LastSuccessfulAt = null,
                },
                FreshnessKind = ClaudeUsageFreshnessKind.None,
            };
        }
        _currentSource = selection.Source;
        return new(_generation, selection.Snapshot, selection);
    }

    private void Publish(ClaudeRuntimeSnapshot snapshot)
    {
        try { SnapshotChanged?.Invoke(snapshot); }
        catch (Exception) { }
    }

    private void DetachCoordinator()
    {
        _coordinator = null;
    }
}

internal sealed class ClaudeActiveSourceConfiguration
{
    public ClaudeActiveSourceConfiguration(
        string? executablePath,
        string bridgePath,
        TimeSpan startupTimeout)
    {
        ExecutablePath = NormalizeExecutable(executablePath);
        BridgePath = Path.GetFullPath(
            bridgePath ?? throw new ArgumentNullException(nameof(bridgePath)));
        StartupTimeout = startupTimeout;
    }

    public string ExecutablePath { get; }

    public string BridgePath { get; }

    public TimeSpan StartupTimeout { get; }

    public bool IsEquivalentTo(ClaudeActiveSourceConfiguration other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return
            string.Equals(ExecutablePath, other.ExecutablePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(BridgePath, other.BridgePath, StringComparison.OrdinalIgnoreCase) &&
            StartupTimeout == other.StartupTimeout;
    }

    private static string NormalizeExecutable(string? executablePath)
    {
        string value = executablePath?.Trim() ?? string.Empty;
        return value.Length > 0 && Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : value;
    }
}

internal readonly record struct ClaudeRuntimeSnapshot(
    long Generation,
    UsageSnapshot Snapshot,
    ClaudeUsageSelection Selection)
{
    public ClaudeRuntimeSnapshot(long generation, UsageSnapshot snapshot)
        : this(generation, snapshot, new(
            snapshot,
            null,
            false,
            "LEGACY",
            ClaudeUsageFreshnessKind.None,
            null,
            null,
            null))
    {
    }
}

public readonly record struct ClaudeRuntimeHealth(
    ClaudeUsageSourceKind? SelectedSource,
    DateTimeOffset? ActiveLastAttemptAt,
    string? ActiveFailureReason,
    DateTimeOffset? PassiveLastReceivedAt);
