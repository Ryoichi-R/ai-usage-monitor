using AiUsageMonitor.App.UI.Runtime;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class ClaudePollScheduleTests
{
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
