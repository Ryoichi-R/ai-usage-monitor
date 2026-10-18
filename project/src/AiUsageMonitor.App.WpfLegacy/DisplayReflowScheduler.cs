using System.Diagnostics;
using System.Windows.Threading;

namespace AiUsageMonitor.App;

internal interface IDisplayReflowTimer
{
    event EventHandler? Tick;
    void Start(TimeSpan interval);
    void Stop();
}

internal interface IDisplayReflowTimerFactory
{
    IDisplayReflowTimer Create();
}

internal sealed class DispatcherDisplayReflowTimerFactory : IDisplayReflowTimerFactory
{
    private readonly Dispatcher _dispatcher;

    internal DispatcherDisplayReflowTimerFactory(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public IDisplayReflowTimer Create() => new DispatcherDisplayReflowTimer(_dispatcher);

    private sealed class DispatcherDisplayReflowTimer : IDisplayReflowTimer
    {
        private readonly DispatcherTimer _timer;

        internal DispatcherDisplayReflowTimer(Dispatcher dispatcher) =>
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);

        public event EventHandler? Tick
        {
            add => _timer.Tick += value;
            remove => _timer.Tick -= value;
        }

        public void Start(TimeSpan interval)
        {
            _timer.Interval = interval;
            _timer.Start();
        }

        public void Stop() => _timer.Stop();
    }
}

/// <summary>表示変更通知の集約と、drag/visibility/終了ライフサイクルを所有する。</summary>
internal sealed class DisplayReflowScheduler
{
    internal const int DisplayDebounceMilliseconds = 750;
    internal const int DisplayWaveDeadlineMilliseconds = 2_000;

    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _executeReflow;
    private readonly Func<bool> _isVisible;
    private readonly Func<bool> _isDragging;
    private readonly IDisplayReflowTimerFactory _timerFactory;
    private readonly Func<long> _clock;
    private IDisplayReflowTimer? _debounceTimer;
    private IDisplayReflowTimer? _deadlineTimer;
    private IDisplayReflowTimer? _retryStartTimer;
    private bool _waveActive;
    private bool _waveIsRetry;
    private bool _retryBudgetUsed;
    private bool _deferredAfterDrag;
    private bool _disposed;
    private long _nextWaveId;
    private long _activeWaveId;

    internal DisplayReflowScheduler(
        Dispatcher dispatcher,
        Func<bool> executeReflow,
        Func<bool> isVisible,
        Func<bool> isDragging,
        IDisplayReflowTimerFactory? timerFactory = null,
        Func<long>? clock = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _executeReflow = executeReflow ?? throw new ArgumentNullException(nameof(executeReflow));
        _isVisible = isVisible ?? throw new ArgumentNullException(nameof(isVisible));
        _isDragging = isDragging ?? throw new ArgumentNullException(nameof(isDragging));
        _timerFactory = timerFactory ?? new DispatcherDisplayReflowTimerFactory(dispatcher);
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    internal bool HasPendingWork =>
        _waveActive || _debounceTimer is not null || _deadlineTimer is not null || _retryStartTimer is not null || _deferredAfterDrag;

    internal long ActiveWaveIdForTest => _activeWaveId;
    internal bool ActiveWaveIsRetryForTest => _waveIsRetry;
    internal long ActiveWaveId => _activeWaveId;
    internal bool ActiveWaveIsRetry => _waveIsRetry;

    internal void NotifyDisplayChange()
    {
        if (_disposed) return;
        _dispatcher.VerifyAccess();
        _retryBudgetUsed = false;
        StopTimer(ref _retryStartTimer);
        if (!_isVisible()) return;
        if (_isDragging())
        {
            _deferredAfterDrag = true;
            return;
        }

        if (!_waveActive)
        {
            StartWave(retry: false);
        }
        RestartTimer(ref _debounceTimer, DisplayDebounceMilliseconds, OnDebounceTimer);
        DisplayReflowEventSource.Log.Notification(_activeWaveId, _clock(), _waveIsRetry, "display-message");
    }

    internal void NotifyDpiChanged() => NotifyDisplayChange();

    internal void SetDragging(bool dragging)
    {
        if (_disposed) return;
        _dispatcher.VerifyAccess();
        if (dragging)
        {
            bool hadPendingWork = HasPendingWork;
            CancelWave();
            _deferredAfterDrag = hadPendingWork;
            return;
        }

        if (_deferredAfterDrag)
        {
            _deferredAfterDrag = false;
            EvaluateOnce(retry: false, timerName: "drag-complete");
        }
    }

    internal void SetVisible(bool visible)
    {
        if (_disposed) return;
        _dispatcher.VerifyAccess();
        if (!visible) CancelWave();
    }

    internal void Cancel()
    {
        if (_disposed) return;
        _dispatcher.VerifyAccess();
        CancelWave();
        _deferredAfterDrag = false;
        _retryBudgetUsed = false;
    }

    private void StartWave(bool retry)
    {
        _waveActive = true;
        _waveIsRetry = retry;
        _activeWaveId = ++_nextWaveId;
        RestartTimer(ref _deadlineTimer, DisplayWaveDeadlineMilliseconds, OnDeadlineTimer);
        DisplayReflowEventSource.Log.WaveStarted(_activeWaveId, _clock(), retry);
    }

    private void OnDebounceTimer(object? sender, EventArgs e) =>
        EvaluateOnce(_waveIsRetry, "debounce");

    private void OnDeadlineTimer(object? sender, EventArgs e) =>
        EvaluateOnce(_waveIsRetry, "deadline");

    private void EvaluateOnce(bool retry, string timerName)
    {
        if (_disposed) return;
        StopTimer(ref _debounceTimer);
        StopTimer(ref _deadlineTimer);
        _waveActive = false;
        DisplayReflowEventSource.Log.TimerFired(_activeWaveId, _clock(), retry, timerName);
        if (!_isVisible() || _isDragging())
        {
            _deferredAfterDrag = true;
            return;
        }

        bool succeeded;
        try
        {
            succeeded = _executeReflow();
        }
        catch (Exception exception)
        {
            succeeded = false;
            DisplayReflowEventSource.Log.InteropFailure(
                _activeWaveId, _clock(), retry, "reflow-callback", exception.HResult);
        }

        DisplayReflowEventSource.Log.Placement(
            _activeWaveId, _clock(), retry, succeeded ? "requested" : "failed");
        // Windowsの回転確定直後は、最初の再配置要求が成功しても
        // GetMonitorInfoのworking areaが変更前の矩形を返すことがある。
        // 成否にかかわらず通常waveの後に1回だけsettle waveを実行し、
        // OS側の表示状態が安定した後の矩形で再配置を確定する。
        if (!retry && !_retryBudgetUsed)
        {
            _retryBudgetUsed = true;
            RestartTimer(ref _retryStartTimer, DisplayDebounceMilliseconds, OnRetryStartTimer);
            if (succeeded)
            {
                DisplayReflowEventSource.Log.Placement(
                    _activeWaveId, _clock(), false, "settle-retry-scheduled");
            }
            else
            {
                DisplayReflowEventSource.Log.InteropFailure(
                    _activeWaveId, _clock(), false, "retry-scheduled", 0);
            }
        }
    }

    private void OnRetryStartTimer(object? sender, EventArgs e)
    {
        StopTimer(ref _retryStartTimer);
        if (_disposed || !_isVisible() || _isDragging())
        {
            _deferredAfterDrag = true;
            return;
        }

        // 失敗した通常waveとは別のwave ID・別deadlineを開始する。
        StartWave(retry: true);
        RestartTimer(ref _debounceTimer, DisplayDebounceMilliseconds, OnDebounceTimer);
    }

    private void RestartTimer(
        ref IDisplayReflowTimer? timer,
        int milliseconds,
        EventHandler callback)
    {
        StopTimer(ref timer);
        timer = _timerFactory.Create();
        timer.Tick += callback;
        timer.Start(TimeSpan.FromMilliseconds(milliseconds));
    }

    private static void StopTimer(ref IDisplayReflowTimer? timer)
    {
        timer?.Stop();
        timer = null;
    }

    private void CancelWave()
    {
        StopTimer(ref _debounceTimer);
        StopTimer(ref _deadlineTimer);
        StopTimer(ref _retryStartTimer);
        _waveActive = false;
        _waveIsRetry = false;
    }

    internal void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;
        CancelWave();
        _deferredAfterDrag = false;
    }
}
