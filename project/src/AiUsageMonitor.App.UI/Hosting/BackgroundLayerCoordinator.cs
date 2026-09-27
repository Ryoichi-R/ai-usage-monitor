using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AiUsageMonitor.App.UI.Hosting;

/// <summary>分離背景の配置・表示・寿命をメインウィジェットへ同期する。</summary>
internal sealed class BackgroundLayerCoordinator : IDisposable
{
    private readonly MainWindow _main;
    private readonly IWidgetLayerController _layer;
    private AppSettings _settings = new AppSettings().Normalized();
    private BackgroundPresentationMode _mode;
    private bool _disposed;
    private bool _queued;

    internal BackgroundWindow Background { get; }

    public BackgroundLayerCoordinator(MainWindow main, IWidgetLayerController layer, Action<string, Exception?> diagnostic)
    {
        _main = main;
        _layer = layer;
        Background = new BackgroundWindow(layer, diagnostic);
        _main.PropertyChanged += OnPropertyChanged;
        _main.PositionChanged += OnPositionChanged;
        _main.InformationBoundsChanged += Schedule;
        _main.UserMoveStarted += Suspend;
        _main.UserMoveCompleted += Schedule;
        _main.Closed += OnClosed;
    }

    public void Apply(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _settings = settings.Normalized();
        _mode = BackgroundPresentationPolicy.Evaluate(_settings, splitAvailable: true);
        _main.SetInlineBackground(_settings, _mode);
        Background.ApplyAppearance(_settings);
        Sync();
        Schedule();
    }

    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && !_main.IsVisible)
            Background.SetPresentationRequested(false);
        Schedule();
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => Schedule();

    private void Suspend() => Background.SetPresentationRequested(false);

    private void Schedule()
    {
        if (_disposed || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (!_disposed) Sync();
        }, DispatcherPriority.Render);
    }

    internal void Sync()
    {
        if (_disposed) return;
        if (_mode != BackgroundPresentationMode.Split || !_main.IsVisible || _main.IsUserMoving)
        {
            Background.SetPresentationRequested(false);
            return;
        }
        InformationGeometry info = _main.GetInformationGeometry();
        if (info.WidthDip <= 0 || info.HeightDip <= 0) return;
        double ratio = _settings.BackgroundFillMode == BackgroundFillMode.EdgeFade
            ? Math.Clamp(_settings.BackgroundEdgeFadePercent / 100d, .05, .5) : 0;
        double scale = _main.DesktopScaling > 0 ? _main.DesktopScaling : 1d;
        Background.Width = info.WidthDip * (1 + 2 * ratio);
        Background.Height = info.HeightDip * (1 + 2 * ratio);
        Background.Position = new PixelPoint(
            _main.Position.X + (int)Math.Round((info.InsetXDip - info.WidthDip * ratio) * scale),
            _main.Position.Y + (int)Math.Round((info.InsetYDip - info.HeightDip * ratio) * scale));
        Background.SetPresentationRequested(true);
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _main.PropertyChanged -= OnPropertyChanged;
        _main.PositionChanged -= OnPositionChanged;
        _main.InformationBoundsChanged -= Schedule;
        _main.UserMoveStarted -= Suspend;
        _main.UserMoveCompleted -= Schedule;
        _main.Closed -= OnClosed;
        Background.Close();
        _layer.Dispose();
    }
}
