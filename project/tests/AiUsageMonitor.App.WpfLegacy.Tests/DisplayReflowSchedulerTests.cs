namespace AiUsageMonitor.App.Tests;

public sealed class DisplayReflowSchedulerTests
{
    [Fact]
    public void OnlyDisplayAndSetWorkAreaMessagesTriggerReflow()
    {
        Assert.True(DisplayReflowMessageFilter.IsDisplayOrWorkAreaChange(
            DisplayReflowMessageFilter.WmDisplayChange,
            0));
        Assert.True(DisplayReflowMessageFilter.IsDisplayOrWorkAreaChange(
            DisplayReflowMessageFilter.WmSettingChange,
            DisplayReflowMessageFilter.SpiSetWorkArea));
        Assert.False(DisplayReflowMessageFilter.IsDisplayOrWorkAreaChange(
            DisplayReflowMessageFilter.WmSettingChange,
            0x1234));
        Assert.False(DisplayReflowMessageFilter.IsDisplayOrWorkAreaChange(0x0046, 0));
    }

    [Fact]
    public void WaveConstantsKeepTrailingEdgeAndIndependentDeadlineDistinct()
    {
        Assert.Equal(750, DisplayReflowScheduler.DisplayDebounceMilliseconds);
        Assert.Equal(2_000, DisplayReflowScheduler.DisplayWaveDeadlineMilliseconds);
        Assert.NotEqual(
            DisplayReflowScheduler.DisplayDebounceMilliseconds,
            DisplayReflowScheduler.DisplayWaveDeadlineMilliseconds);
    }

    [Fact]
    public void NotificationWaveCoalescesAndDeadlineCanFireWithoutSleeping()
    {
        int executions = 0;
        var timers = new FakeTimerFactory();
        var scheduler = CreateScheduler(timers, () => { executions++; return true; });

        scheduler.NotifyDisplayChange();
        scheduler.NotifyDisplayChange();
        Assert.Equal(1, scheduler.ActiveWaveId);
        Assert.Equal(2, timers.RunningTimers.Length);

        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);

        Assert.Equal(1, executions);
        Assert.True(scheduler.HasPendingWork);

        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);
        Assert.True(scheduler.ActiveWaveIsRetry);
        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);

        Assert.Equal(2, executions);
        Assert.False(scheduler.HasPendingWork);
    }

    [Fact]
    public void SuccessfulNormalWaveStartsOneSettleRetryWave()
    {
        int executions = 0;
        var timers = new FakeTimerFactory();
        var scheduler = CreateScheduler(timers, () => { executions++; return true; });

        scheduler.NotifyDisplayChange();
        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);

        Assert.Equal(1, executions);
        Assert.Contains(
            timers.RunningTimers,
            timer => timer.IntervalMilliseconds == DisplayReflowScheduler.DisplayDebounceMilliseconds);

        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);
        Assert.True(scheduler.ActiveWaveIsRetry);
        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);

        Assert.Equal(2, executions);
        Assert.False(scheduler.HasPendingWork);
    }

    [Fact]
    public void DeadlineEndsContinuousWaveAndLaterNotificationStartsNewWave()
    {
        int executions = 0;
        var timers = new FakeTimerFactory();
        var scheduler = CreateScheduler(timers, () => { executions++; return true; });

        scheduler.NotifyDisplayChange();
        timers.Fire(DisplayReflowScheduler.DisplayWaveDeadlineMilliseconds);
        long firstWave = scheduler.ActiveWaveId;
        scheduler.NotifyDisplayChange();
        long secondWave = scheduler.ActiveWaveId;
        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);

        Assert.Equal(2, executions);
        Assert.NotEqual(firstWave, secondWave);
    }

    [Fact]
    public void FailedWaveStartsOneIndependentRetryWaveWithItsOwnDeadline()
    {
        int executions = 0;
        var timers = new FakeTimerFactory();
        var scheduler = CreateScheduler(timers, () => ++executions > 1);

        scheduler.NotifyDisplayChange();
        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);
        Assert.Equal(1, executions);
        Assert.Contains(timers.RunningTimers, timer => timer.IntervalMilliseconds == DisplayReflowScheduler.DisplayDebounceMilliseconds);

        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);
        long retryWave = scheduler.ActiveWaveId;
        Assert.True(scheduler.ActiveWaveIsRetry);
        Assert.NotEqual(1L, retryWave);
        Assert.Contains(timers.RunningTimers, timer => timer.IntervalMilliseconds == DisplayReflowScheduler.DisplayWaveDeadlineMilliseconds);

        timers.Fire(DisplayReflowScheduler.DisplayDebounceMilliseconds);

        Assert.Equal(2, executions);
        Assert.False(scheduler.HasPendingWork);
    }

    [Fact]
    public void DragDefersOnceAndHiddenCancelPreventsCallbackAfterShutdown()
    {
        int executions = 0;
        var timers = new FakeTimerFactory();
        var scheduler = CreateScheduler(timers, () => { executions++; return true; });

        scheduler.NotifyDisplayChange();
        scheduler.SetDragging(true);
        scheduler.SetDragging(false);
        Assert.Equal(1, executions);

        scheduler.NotifyDisplayChange();
        scheduler.SetVisible(false);
        Assert.Empty(timers.RunningTimers);
        scheduler.Shutdown();
        foreach (FakeTimer timer in timers.AllTimers) timer.FireIfRunning();

        Assert.Equal(1, executions);
    }

    private static DisplayReflowScheduler CreateScheduler(
        FakeTimerFactory timers,
        Func<bool> execute)
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        return new DisplayReflowScheduler(
            dispatcher,
            execute,
            () => true,
            () => false,
            timers,
            () => 1);
    }

    private sealed class FakeTimerFactory : IDisplayReflowTimerFactory
    {
        internal List<FakeTimer> AllTimers { get; } = [];
        internal FakeTimer[] RunningTimers => AllTimers.Where(timer => timer.IsRunning).ToArray();

        public IDisplayReflowTimer Create()
        {
            var timer = new FakeTimer();
            AllTimers.Add(timer);
            return timer;
        }

        internal void Fire(int intervalMilliseconds)
        {
            FakeTimer timer = RunningTimers.Single(candidate => candidate.IntervalMilliseconds == intervalMilliseconds);
            timer.Fire();
        }
    }

    private sealed class FakeTimer : IDisplayReflowTimer
    {
        internal bool IsRunning { get; private set; }
        internal int IntervalMilliseconds { get; private set; }
        public event EventHandler? Tick;

        public void Start(TimeSpan interval)
        {
            IntervalMilliseconds = (int)interval.TotalMilliseconds;
            IsRunning = true;
        }

        public void Stop() => IsRunning = false;

        internal void Fire()
        {
            if (!IsRunning) return;
            IsRunning = false;
            Tick?.Invoke(this, EventArgs.Empty);
        }

        internal void FireIfRunning() => Fire();
    }
}
