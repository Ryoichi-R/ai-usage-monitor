namespace AiUsageMonitor.App;

internal sealed class ClaudeManualRefreshState
{
    private readonly object _sync = new();
    private RefreshPhase _phase;
    private Task? _currentClaudeTask;
    private bool _pending;
    private bool _draining;

    public void Enter(RefreshPhase phase, Task? currentClaudeTask = null)
    {
        lock (_sync)
        {
            _phase = phase;
            _currentClaudeTask = phase == RefreshPhase.Claude
                ? currentClaudeTask
                : null;
        }
    }

    public ManualRefreshBusyDecision RequestWhileBusy()
    {
        lock (_sync)
        {
            if (_phase == RefreshPhase.Claude && _currentClaudeTask is { } current)
                return new(current, false);
            if (_draining)
                return new(null, false);
            _pending = true;
            return new(null, true);
        }
    }

    public bool CompleteOwner()
    {
        lock (_sync)
        {
            _phase = RefreshPhase.Idle;
            _currentClaudeTask = null;
            bool followUp = _pending;
            _pending = false;
            return followUp;
        }
    }

    public void BeginDrain()
    {
        lock (_sync) _draining = true;
    }

    public void EndDrain()
    {
        lock (_sync) _draining = false;
    }
}

internal readonly record struct ManualRefreshBusyDecision(
    Task? JoinTask,
    bool WasCoalesced);
