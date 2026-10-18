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
}
