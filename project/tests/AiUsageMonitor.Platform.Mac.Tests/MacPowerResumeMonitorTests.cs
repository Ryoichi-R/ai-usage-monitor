using System.Collections.Concurrent;

namespace AiUsageMonitor.Platform.Mac.Tests;

public sealed class MacPowerResumeMonitorTests
{
    [Fact]
    public async Task DetectsSleepDefersInDarkWakeAndContinuesAfterSamplingFailure()
    {
        var codes = new ConcurrentQueue<string>();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int samples = 0, requests = 0, displayChecks = 0;
        TimeSpan ReadClock() => ++samples switch
        {
            1 or 2 => TimeSpan.Zero,
            3 => TimeSpan.FromSeconds(9),
            4 or 5 => throw new IOException("clock unavailable"),
            6 or 7 or 8 => TimeSpan.FromSeconds(30),
            _ => FinishAtStableOffset(),
        };
        TimeSpan FinishAtStableOffset()
        {
            if (samples == 11) finished.TrySetResult();
            return TimeSpan.FromSeconds(50);
        }

        await using var monitor = new MacPowerResumeMonitor(ReadClock, () => ++displayChecks switch
        {
            1 or 2 => true,
            3 => false,
            _ => null, // no online display or unknown state
        }, () => requests++, (code, _) => codes.Enqueue(code), TimeSpan.FromMilliseconds(10));
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, requests);
        Assert.Equal(4, displayChecks);
        Assert.Equal(["power-resume-monitor-failure", "power-resume-detected", "power-resume-deferred",
            "power-resume-detected"], codes.ToArray());
    }

    [Fact]
    public async Task StoppingDuringDisplayReadDrainsTheCallbackWithoutSendingARequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishDisplayRead = new ManualResetEventSlim();
        int samples = 0, requests = 0;
        var monitor = new MacPowerResumeMonitor(() => TimeSpan.FromSeconds(samples++ == 0 ? 0 : 30), () =>
        {
            entered.TrySetResult();
            finishDisplayRead.Wait(TimeSpan.FromSeconds(5));
            return false;
        }, () => requests++, (_, _) => { }, TimeSpan.FromMilliseconds(10));

        Task? stop = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop = monitor.DisposeAsync().AsTask();
            Assert.False(stop.IsCompleted);
        }
        finally
        {
            finishDisplayRead.Set();
            await (stop ?? monitor.DisposeAsync().AsTask());
        }
        Assert.Equal(0, requests);
    }
}
