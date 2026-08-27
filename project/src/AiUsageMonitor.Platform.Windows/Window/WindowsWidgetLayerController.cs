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

    public WindowsWidgetLayerController()
        : this(handle => new TopmostWindowController(handle), NativeWindowLayerApi.Instance)
    {
    }

    internal WindowsWidgetLayerController(Func<nint, TopmostWindowController> topmostFactory, IWindowLayerApi bottomMostApi)
    {
        _topmostFactory = topmostFactory;
        _bottomMostApi = bottomMostApi;
    }

    public WidgetLayerHealth Health => _topmost is { } topmost ? MapHealth(topmost.Health) : default;

    public event Action<WidgetLayerHealth>? HealthChanged;

    public void Attach(nint nativeWindowHandle)
    {
        ThrowIfDisposed();
        if (_windowHandle != 0)
            throw new InvalidOperationException("Already attached to a window handle.");
        _windowHandle = nativeWindowHandle;
        _topmost = _topmostFactory(nativeWindowHandle);
        _topmost.HealthChanged += health => HealthChanged?.Invoke(MapHealth(health));
    }

    public void SetLayerMode(WidgetLayerMode mode)
    {
        ThrowIfDisposed();
        EnsureAttached();
        _mode = mode;
        _topmost!.SetEnabled(mode == WidgetLayerMode.AlwaysOnTop);
        if (mode != WidgetLayerMode.AlwaysOnTop)
            BottomMostStrategy.Apply(_windowHandle, ToLayerStrategy(mode), _bottomMostApi);
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
            : BottomMostStrategy.Apply(_windowHandle, ToLayerStrategy(_mode), _bottomMostApi);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _topmost?.Dispose();
        HealthChanged = null;
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
