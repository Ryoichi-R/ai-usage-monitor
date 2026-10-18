namespace AiUsageMonitor.Platform.Windows;

/// <summary>
/// 多重起動で終了する後続インスタンスから、既存インスタンスへ「ウィジェットを見せて」と通知する名前付きevent。
/// 後続インスタンスは何も表示せず終了するため、この通知がないと起動に失敗したように見える。
/// </summary>
public sealed class WindowsInstanceActivationChannel : IDisposable
{
    private readonly EventWaitHandle _event;
    private readonly RegisteredWaitHandle _registration;
    private bool _disposed;

    private WindowsInstanceActivationChannel(EventWaitHandle activationEvent, Action onActivationRequested)
    {
        _event = activationEvent;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _event,
            (_, _) => onActivationRequested(),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>既存インスタンス側で通知の待受を開始する。callbackはthread poolで呼ばれる。</summary>
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        using (var unregistered = new ManualResetEvent(false))
        {
            // 実行中callbackの完了を待ってからhandleを閉じる。
            if (_registration.Unregister(unregistered)) unregistered.WaitOne();
        }
        _event.Dispose();
    }
}
