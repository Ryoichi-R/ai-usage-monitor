namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsSingleInstanceGuardTests
{
    [Fact]
    public void FirstGuardAcquiresAndASecondGuardOnTheSameNameDoesNot()
    {
        string mutexName = "Local\\AiUsageMonitorTests-" + Guid.NewGuid().ToString("N");
        using var first = new WindowsSingleInstanceGuard(mutexName);
        using var second = new WindowsSingleInstanceGuard(mutexName);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
    }

    [Fact]
    public void ANewGuardAcquiresAfterThePreviousOneIsDisposed()
    {
        string mutexName = "Local\\AiUsageMonitorTests-" + Guid.NewGuid().ToString("N");
        var first = new WindowsSingleInstanceGuard(mutexName);
        Assert.True(first.TryAcquire());
        first.Dispose();

        using var second = new WindowsSingleInstanceGuard(mutexName);
        Assert.True(second.TryAcquire());
    }
}
