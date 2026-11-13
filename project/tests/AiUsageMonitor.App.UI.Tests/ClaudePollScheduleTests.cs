using AiUsageMonitor.App.UI.Runtime;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class ClaudePollScheduleTests
{
    [Fact]
    public async Task ExpiredRetryIsConsumedOnceAndChangedDeadlineStillWakesThePoll()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset? retry = now.AddSeconds(-1);
        using var schedule = new ClaudePollSchedule(() => now);
        using var cancel = new CancellationTokenSource();
        Assert.False(await schedule.WaitAsync(now.AddMinutes(5), () => retry, cancel.Token));
        schedule.Changed(); // The refresh owner notifies even when its source kept the old deadline.
        Task<bool> next = schedule.WaitAsync(now.AddMinutes(5), () => retry, cancel.Token);
        try
        {
            Assert.False(next.IsCompleted);
            retry = now.AddSeconds(-.5);
            schedule.Changed();
            Assert.False(await next.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally { cancel.Cancel(); }
    }

    [Fact]
    public async Task ConsumedRetryFallsBackToInspectionAndStillAllowsResume()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset retry = now.AddSeconds(-1);
        using var schedule = new ClaudePollSchedule(() => now);
        using var cancel = new CancellationTokenSource();
        await schedule.WaitAsync(now.AddMinutes(5), () => retry, cancel.Token);
        Task<bool> inspection = schedule.WaitAsync(now.AddMinutes(5), () => retry, cancel.Token);
        try
        {
            Assert.False(inspection.IsCompleted);
            now = now.AddMinutes(5);
            schedule.Changed();
            Assert.False(await inspection.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Task<bool> next = schedule.WaitAsync(now.AddMinutes(5), () => retry, cancel.Token);
            Assert.False(next.IsCompleted);
            schedule.RequestResume();
            Assert.True(await next.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally { cancel.Cancel(); }
    }

    [Fact]
    public async Task ResumeInterruptsAnUnexpiredWaitAndCoalescesUntilConsumed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using var schedule = new ClaudePollSchedule(() => now);
        using var cancel = new CancellationTokenSource();
        Task<bool> wait = schedule.WaitAsync(now.AddMinutes(5), () => now.AddMinutes(30), cancel.Token);
        Assert.False(wait.IsCompleted);

        schedule.RequestResume();
        schedule.RequestResume();
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        schedule.ConsumeResume();

        Task<bool> next = schedule.WaitAsync(now.AddMinutes(5), () => null, cancel.Token);
        Assert.False(next.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
    }

    [Fact]
    public async Task ManualFailureWakesFiveMinuteSleepAtNewOneMinuteDeadline()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow, started = now;
        DateTimeOffset? retry = null;
        using var schedule = new ClaudePollSchedule(() => now);
        Task wait = schedule.WaitAsync(started.AddMinutes(5), () => retry, CancellationToken.None);
        retry = started.AddMinutes(1); schedule.Changed();
        Assert.False(wait.IsCompleted);
        now = started.AddMinutes(1); schedule.Changed();
        await wait.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Fact]
    public async Task ManualSuccessRemovesOldRetryWithoutPrematureAcquisition()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow, started = now;
        DateTimeOffset? retry = started.AddMinutes(1);
        using var schedule = new ClaudePollSchedule(() => now);
        Task wait = schedule.WaitAsync(started.AddMinutes(5), () => retry, CancellationToken.None);
        retry = null; now = started.AddMinutes(1); schedule.Changed();
        Assert.False(wait.IsCompleted);
        now = started.AddMinutes(5); schedule.Changed();
        await wait.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    }
}
