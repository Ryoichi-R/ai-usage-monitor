using System.Windows;
using System.Windows.Threading;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Windows.Window;

namespace AiUsageMonitor.App;

internal sealed class BackgroundLayerCoordinator : IDisposable
{
    private readonly MainWindow _mainWindow;
    private readonly BackgroundWindow _backgroundWindow;
    private readonly Dispatcher _dispatcher;
    private AppSettings _settings = new AppSettings().Normalized();
    private BackgroundPresentationMode _mode;
    private DispatcherOperation? _syncOperation;
    private bool _disposed;

    internal BackgroundLayerCoordinator(MainWindow mainWindow, Dispatcher dispatcher, Action<string, Exception?>? diagnostic = null)
    {
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _backgroundWindow = new BackgroundWindow(diagnostic ?? ((_, _) => { }));
        _mainWindow.LocationChanged += OnBoundsChanged;
        _mainWindow.SizeChanged += OnBoundsChanged;
        _mainWindow.DpiChanged += OnDpiChanged;
        _mainWindow.IsVisibleChanged += OnVisibilityChanged;
        _mainWindow.InformationBoundsChanged += OnInformationBoundsChanged;
        _mainWindow.UserMoveStarted += SyncNow;
        _mainWindow.UserMoveCompleted += SyncNow;
        _mainWindow.Closed += OnMainWindowClosed;
    }

    internal BackgroundPresentationMode Mode => _mode;
    internal BackgroundWindow BackgroundWindow => _backgroundWindow;

    internal void Apply(AppSettings settings)
    {
        ThrowIfDisposed();
        _settings = settings.Normalized();
        _mode = BackgroundPresentationPolicy.Evaluate(_settings, splitAvailable: true);
        if (_mode != BackgroundPresentationMode.Split)
        {
            _backgroundWindow.SetPresentationRequested(false);
            _mainWindow.SetInlineBackground(_settings, _mode);
            return;
        }

        _mainWindow.SetInlineBackground(_settings, BackgroundPresentationMode.Split);
        _backgroundWindow.ApplyAppearance(_settings);
        SyncNow();
        _backgroundWindow.SetPresentationRequested(_mainWindow.IsVisible);
    }

    internal void SetWidgetVisible(bool visible)
    {
        ThrowIfDisposed();
        if (!visible)
        {
            _backgroundWindow.SetPresentationRequested(false);
            _mainWindow.Hide();
            return;
        }
        _mainWindow.Show();
        if (_mode == BackgroundPresentationMode.Split)
        {
            SyncNow();
            _backgroundWindow.SetPresentationRequested(true);
        }
    }

    internal void SetRepairSuspended(bool suspended)
    {
        ThrowIfDisposed();
        _backgroundWindow.SetRepairSuspended(suspended);
    }

    internal void Reapply()
    {
        ThrowIfDisposed();
        if (_mode == BackgroundPresentationMode.Split && _mainWindow.IsVisible)
        {
            SyncNow();
            _backgroundWindow.SetPresentationRequested(true);
        }
    }

    internal void SyncNow()
    {
        ThrowIfDisposed();
        if (_syncOperation?.Status == DispatcherOperationStatus.Pending) _syncOperation.Abort();
        _syncOperation = null;
        CopyBounds();
    }

    private void CopyBounds()
    {
        if (_mode != BackgroundPresentationMode.Split) return;
        Rect information = _mainWindow.GetInformationBoundsInScreenDip();
        double ratio = _settings.BackgroundFillMode == BackgroundFillMode.EdgeFade
            ? Math.Clamp(_settings.BackgroundEdgeFadePercent / 100d, .05, .5)
            : 0;
        _backgroundWindow.Left = information.Left - information.Width * ratio;
        _backgroundWindow.Top = information.Top - information.Height * ratio;
        _backgroundWindow.Width = information.Width * (1d + (2d * ratio));
        _backgroundWindow.Height = information.Height * (1d + (2d * ratio));
    }

    private void ScheduleSync()
    {
        if (_disposed || _mode != BackgroundPresentationMode.Split ||
            _syncOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing) return;
        _syncOperation = _dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (!_disposed && _mode == BackgroundPresentationMode.Split) CopyBounds();
            _syncOperation = null;
        });
    }

    private void OnBoundsChanged(object? sender, EventArgs e) => ScheduleSync();
    private void OnDpiChanged(object? sender, System.Windows.DpiChangedEventArgs e) => ScheduleSync();
    private void OnInformationBoundsChanged() => ScheduleSync();

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed || _mode != BackgroundPresentationMode.Split) return;
        if (_mainWindow.IsVisible)
        {
            _backgroundWindow.ApplyAppearance(_settings);
            SyncNow();
            _backgroundWindow.SetPresentationRequested(true);
        }
        else _backgroundWindow.SetPresentationRequested(false);
    }

    private void OnMainWindowClosed(object? sender, EventArgs e) => Dispose();

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mainWindow.LocationChanged -= OnBoundsChanged;
        _mainWindow.SizeChanged -= OnBoundsChanged;
        _mainWindow.DpiChanged -= OnDpiChanged;
        _mainWindow.IsVisibleChanged -= OnVisibilityChanged;
        _mainWindow.InformationBoundsChanged -= OnInformationBoundsChanged;
        _mainWindow.UserMoveStarted -= SyncNow;
        _mainWindow.UserMoveCompleted -= SyncNow;
        _mainWindow.Closed -= OnMainWindowClosed;
        if (_syncOperation?.Status == DispatcherOperationStatus.Pending) _syncOperation.Abort();
        _syncOperation = null;
        _backgroundWindow.Close();
    }
}
