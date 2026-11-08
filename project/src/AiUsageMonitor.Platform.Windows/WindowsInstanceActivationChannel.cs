namespace AiUsageMonitor.Platform.Windows;

/// <summary>
/// 多重起動で終了する後続インスタンスから、既存インスタンスへ「ウィジェットを見せて」と通知する名前付きevent。
/// 後続インスタンスは何も表示せず終了するため、この通知がないと起動に失敗したように見える。
/// </summary>
/// <remarks>
/// 待受には <see cref="ThreadPool.RegisterWaitForSingleObject(WaitHandle, WaitOrTimerCallback, object?, int, bool)"/> を使わない。
/// thread poolの待機threadは、<see cref="RegisteredWaitHandle.Unregister(WaitHandle?)"/> の完了通知の後にも
/// eventのSafeHandle参照を一時的に保持することがあり、その間に <see cref="Dispose"/> しても
/// handleのcloseが遅延して、Dispose直後の <see cref="TrySignal"/> が成功してしまう。
/// 専用threadで待ち、終了をJoinしてからhandleを閉じることで、Dispose完了時点で名前付きeventを確実に閉じる。
/// </remarks>
public sealed class WindowsInstanceActivationChannel : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Action _onActivationRequested;
    private readonly Thread _listener;
    private bool _disposed;

    private WindowsInstanceActivationChannel(EventWaitHandle activationEvent, Action onActivationRequested)
    {
        _event = activationEvent;
        _onActivationRequested = onActivationRequested;
        _listener = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "AiUsageMonitor instance activation listener",
        };
        _listener.Start();
    }

    /// <summary>既存インスタンス側で通知の待受を開始する。callbackは専用の待受threadで呼ばれる。</summary>
    public static WindowsInstanceActivationChannel Listen(string eventName, Action onActivationRequested)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        ArgumentNullException.ThrowIfNull(onActivationRequested);
        return new WindowsInstanceActivationChannel(
            new EventWaitHandle(false, EventResetMode.AutoReset, eventName),
            onActivationRequested);
    }

    /// <summary>後続インスタンス側で通知する。待受側が存在しない（旧版の常駐など）場合はfalse。</summary>
    public static bool TrySignal(string eventName)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);
        if (!EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? existing)) return false;
        using (existing)
        {
            return existing.Set();
        }
    }

    /// <summary>待受を止め、名前付きeventを閉じる。callbackの外から呼んだ場合は、戻った時点でeventは閉じている。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Set();
        // callbackの中から呼ばれた場合は自thread自身を待てない。callbackから戻った後に待受loopがhandleを閉じる。
        if (Thread.CurrentThread != _listener) _listener.Join();
    }

    private void ListenLoop()
    {
        try
        {
            // WaitAnyは同時に通知された中で最小indexを返すため、停止要求を先頭に置いて優先する。
            WaitHandle[] handles = [_stop, _event];
            while (WaitHandle.WaitAny(handles) == 1) _onActivationRequested();
        }
        finally
        {
            // handleの参照を持つのはこのthreadだけなので、ここでのDisposeは即座にcloseされる。
            _event.Dispose();
            _stop.Dispose();
        }
    }
}
