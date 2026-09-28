using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Windows.Window;

/// <summary>
/// TopmostWindowController（常に手前の自己修復）、ClickThroughHelper（クリック透過）、
/// BottomMostStrategy（デスクトップ最背面）を束ね、IWidgetLayerControllerへ適合させる。
/// </summary>
public sealed class WindowsWidgetLayerController : IWidgetLayerController
{
    private readonly Func<nint, TopmostWindowController> _topmostFactory;
    private readonly IWindowLayerApi _bottomMostApi;
    private nint _windowHandle;
    private TopmostWindowController? _topmost;
    private WidgetLayerMode _mode = WidgetLayerMode.Normal;
    private bool _disposed;
    private bool _recoveringTopmost;
    private WidgetLayerHealth _health;

    public WindowsWidgetLayerController()
        : this(handle => new TopmostWindowController(handle), NativeWindowLayerApi.Instance)
    {
    }

    internal WindowsWidgetLayerController(Func<nint, TopmostWindowController> topmostFactory, IWindowLayerApi bottomMostApi)
    {
        _topmostFactory = topmostFactory;
        _bottomMostApi = bottomMostApi;
    }

    public WidgetLayerHealth Health => _health;

    public event Action<WidgetLayerHealth>? HealthChanged;

    public void Attach(nint nativeWindowHandle)
    {
        ThrowIfDisposed();
        if (_windowHandle != 0)
            throw new InvalidOperationException("Already attached to a window handle.");
        _windowHandle = nativeWindowHandle;
        _topmost = _topmostFactory(nativeWindowHandle);
        _topmost.RecoveryRequested += RecoverTopmost;
        _topmost.HealthChanged += health =>
        {
            if (_mode == WidgetLayerMode.AlwaysOnTop) SetHealth(MapHealth(health));
        };
    }

    public void SetLayerMode(WidgetLayerMode mode)
    {
        ThrowIfDisposed();
        EnsureAttached();
        _mode = mode;
        _topmost!.SetEnabled(mode == WidgetLayerMode.AlwaysOnTop);
        if (mode == WidgetLayerMode.AlwaysOnTop)
            RecoverTopmost();
        else
            ApplyNonTopmostLayer();
    }

    public void SetClickThrough(bool enabled)
    {
        ThrowIfDisposed();
        EnsureAttached();
        ClickThroughHelper.Apply(_windowHandle, enabled);
    }

    public bool TryRecoverLayer()
    {
        ThrowIfDisposed();
        if (_windowHandle == 0) return false;
        return _mode == WidgetLayerMode.AlwaysOnTop
            ? _topmost!.TryRecover()
            : ApplyNonTopmostLayer();
    }

    private bool ApplyNonTopmostLayer()
    {
        WindowPositionCallResult result = BottomMostStrategy.ApplyWithResult(_windowHandle, ToLayerStrategy(_mode), _bottomMostApi);
        SetHealth(result.Succeeded ? default : new(true, "SetWindowPos", result.ErrorCode));
        return result.Succeeded;
    }

    private void SetHealth(WidgetLayerHealth health)
    {
        if (_health == health) return;
        _health = health;
        HealthChanged?.Invoke(health);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_topmost is not null) _topmost.RecoveryRequested -= RecoverTopmost;
        _topmost?.Dispose();
        HealthChanged = null;
    }

    // Out-of-context WinEvent callbacks run on the thread that registered the hooks.
    // Apply without activation, and suppress callbacks nested inside SetWindowPos.
    private void RecoverTopmost()
    {
        if (_disposed || _mode != WidgetLayerMode.AlwaysOnTop || _recoveringTopmost) return;
        _recoveringTopmost = true;
        try
        {
            _topmost?.TryRecover();
            if (_topmost is not null) SetHealth(MapHealth(_topmost.Health));
        }
        finally { _recoveringTopmost = false; }
    }

    private void EnsureAttached()
    {
        if (_windowHandle == 0)
            throw new InvalidOperationException("Attach must be called before use.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static LayerStrategy ToLayerStrategy(WidgetLayerMode mode) => mode switch
    {
        WidgetLayerMode.AlwaysOnTop => LayerStrategy.TopMost,
        WidgetLayerMode.AlwaysOnBottom => LayerStrategy.BottomMost,
        _ => LayerStrategy.Normal,
    };

    private static WidgetLayerHealth MapHealth(TopmostHealth health) =>
        new(health.IsDegraded, health.Operation, health.ErrorCode);
}
