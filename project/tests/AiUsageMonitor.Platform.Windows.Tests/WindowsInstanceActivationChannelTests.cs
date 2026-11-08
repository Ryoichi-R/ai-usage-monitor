namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsInstanceActivationChannelTests
{
    [Fact]
    public void SignalInvokesTheListenerCallback()
    {
        string eventName = "Local\\AiUsageMonitorTests-Activate-" + Guid.NewGuid().ToString("N");
        using var received = new ManualResetEventSlim(false);
        using var channel = WindowsInstanceActivationChannel.Listen(eventName, received.Set);

        Assert.True(WindowsInstanceActivationChannel.TrySignal(eventName));
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void ListenerKeepsReceivingRepeatedSignals()
    {
        string eventName = "Local\\AiUsageMonitorTests-Activate-" + Guid.NewGuid().ToString("N");
        using var received = new SemaphoreSlim(0);
        using var channel = WindowsInstanceActivationChannel.Listen(eventName, () => received.Release());

        Assert.True(WindowsInstanceActivationChannel.TrySignal(eventName));
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(WindowsInstanceActivationChannel.TrySignal(eventName));
        Assert.True(received.Wait(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SignalWithoutListenerReturnsFalse()
    {
        string eventName = "Local\\AiUsageMonitorTests-Activate-" + Guid.NewGuid().ToString("N");

        Assert.False(WindowsInstanceActivationChannel.TrySignal(eventName));
    }

    [Fact]
    public void SignalAfterDisposeReturnsFalse()
    {
        string eventName = "Local\\AiUsageMonitorTests-Activate-" + Guid.NewGuid().ToString("N");
        var channel = WindowsInstanceActivationChannel.Listen(eventName, () => { });
        channel.Dispose();

        Assert.False(WindowsInstanceActivationChannel.TrySignal(eventName));
    }

    [Fact]
    public void SignalAfterDisposeReturnsFalseWhileOtherChannelsOpenAndClose()
    {
        // thread poolの待機threadがhandle参照を保持したままDisposeが戻る競合は、
        // 並列に開閉すると数%の頻度で再現した。Dispose完了時点で常にeventが閉じていることを確かめる。
        int signaledAfterDispose = 0;
        Parallel.For(0, 1000, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
        {
            string eventName = "Local\\AiUsageMonitorTests-Activate-" + Guid.NewGuid().ToString("N");
            var channel = WindowsInstanceActivationChannel.Listen(eventName, () => { });
            channel.Dispose();
            if (WindowsInstanceActivationChannel.TrySignal(eventName)) Interlocked.Increment(ref signaledAfterDispose);
        });

        Assert.Equal(0, signaledAfterDispose);
    }

    [Fact]
    public void DisposeFromCallbackClosesTheEventAfterTheCallbackReturns()
    {
        string eventName = "Local\\AiUsageMonitorTests-Activate-" + Guid.NewGuid().ToString("N");
        using var disposed = new ManualResetEventSlim(false);
        WindowsInstanceActivationChannel? channel = null;
        channel = WindowsInstanceActivationChannel.Listen(eventName, () =>
        {
            channel!.Dispose();
            channel.Dispose();
            disposed.Set();
        });

        Assert.True(WindowsInstanceActivationChannel.TrySignal(eventName));
        Assert.True(disposed.Wait(TimeSpan.FromSeconds(5)));
        // callbackの中では自threadを待てないため、callbackから戻った後に待受loopがeventを閉じる。
        Assert.True(SpinWait.SpinUntil(() => !WindowsInstanceActivationChannel.TrySignal(eventName), TimeSpan.FromSeconds(5)));
    }
}
