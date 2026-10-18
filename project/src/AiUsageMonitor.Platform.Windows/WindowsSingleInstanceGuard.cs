using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Windows;

/// <summary>名前付きMutexで多重起動を防止するISingleInstanceGuardのWindows実装。</summary>
public sealed class WindowsSingleInstanceGuard : ISingleInstanceGuard
{
    private readonly Mutex _mutex;
    private readonly bool _acquired;
    private bool _disposed;

    public WindowsSingleInstanceGuard(string mutexName)
    {
        _mutex = new Mutex(initiallyOwned: true, mutexName, out bool createdNew);
        _acquired = createdNew;
    }

    public bool TryAcquire() => _acquired;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mutex.Dispose();
    }
}
