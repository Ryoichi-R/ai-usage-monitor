using AiUsageMonitor.App.UI.Appearance;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Platform;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AiUsageMonitor.App.UI.Views;

/// <summary>
/// 常に手前表示のとき、背景だけを通常ウィンドウの後ろへ分離して描くウィンドウ。WPF版<c>BackgroundWindow</c>の移植で、
/// 最背面・クリック透過・層の自己修復は<see cref="IWidgetLayerController"/>（OS別実装）へ委ねる。
/// 層の維持に失敗している間は背景を隠し（不透明度0）、回復したら戻す。
/// </summary>
public partial class BackgroundWindow : Window
{
    private readonly IWidgetLayerController? _layer;
    private readonly Action<string, Exception?> _diagnostic;
    private readonly DispatcherTimer _repairTimer;
    private AppSettings _settings = new AppSettings().Normalized();
    private bool _requested;
    private bool _suspended;
    private bool _suppressed;
    private bool _attached;
    private bool _closing;

    // XAMLローダー（デザイナー）用。
    public BackgroundWindow()
        : this(null, (_, _) => { })
    {
    }

    public BackgroundWindow(IWidgetLayerController? layer, Action<string, Exception?> diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        InitializeComponent();
        _layer = layer;
        _diagnostic = diagnostic;
        WindowStartupLocation = WindowStartupLocation.Manual;
        _repairTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RunRepairTick());
        Opened += (_, _) => OnOpened();
        Closed += (_, _) => OnClosed();
        if (_layer is not null) _layer.HealthChanged += OnHealthChanged;
    }

    internal bool LayerFailureSuppressed => _suppressed;

    internal bool IsPresentationRequested => _requested;

    /// <summary>ネイティブハンドルを持たないHeadlessテスト環境で、層制御への登録を検証するための代替ハンドル。</summary>
    internal nint? HandleOverrideForTest { get; set; }

    public void ApplyAppearance(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings.Normalized();
        AvaloniaAppearanceBrushes.Apply(BackgroundFadeHost, BackgroundRoot, _settings);
        Opacity = _suppressed ? 0 : _settings.Opacity;
    }

    public void SetPresentationRequested(bool requested)
    {
        Dispatcher.UIThread.VerifyAccess();
        _requested = requested;
        if (!requested)
        {
            Hide();
            return;
        }
        if (!IsVisible) Show();
        Reapply();
    }

    public void SetRepairSuspended(bool suspended)
    {
        Dispatcher.UIThread.VerifyAccess();
        _suspended = suspended;
        if (!suspended && _requested && IsVisible) Reapply();
    }

    internal void RunRepairTick()
    {
        if (_closing || !_requested || _suspended || _layer is null || !_attached) return;
        try
        {
            _layer.TryRecoverLayer();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _diagnostic("background-layer-repair-failure", exception);
        }
        UpdateSuppression(_layer.Health);
    }

    private void OnOpened()
    {
        if (_layer is null) return;
        nint handle = HandleOverrideForTest ?? TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return;
        try
        {
            if (!_attached)
            {
                _layer.Attach(handle);
                _attached = true;
            }
            _layer.SetClickThrough(true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _diagnostic("background-layer-style-failure", exception);
            return;
        }
        Reapply();
        _repairTimer.Start();
    }

    private void Reapply()
    {
        if (_closing || !_requested || _suspended || _layer is null || !_attached) return;
        try
        {
            _layer.SetLayerMode(WidgetLayerMode.AlwaysOnBottom);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _diagnostic("background-layer-apply-failure", exception);
        }
        UpdateSuppression(_layer.Health);
    }

    private void OnHealthChanged(WidgetLayerHealth health) =>
        Dispatcher.UIThread.Post(() => UpdateSuppression(health));

    private void UpdateSuppression(WidgetLayerHealth health)
    {
        if (_closing) return;
        if (health.IsDegraded && !_suppressed)
        {
            _suppressed = true;
            Opacity = 0;
            _diagnostic("background-layer-suppressed", null);
        }
        else if (!health.IsDegraded && _suppressed)
        {
            _suppressed = false;
            Opacity = _settings.Opacity;
            _diagnostic("background-layer-recovered", null);
        }
    }

    private void OnClosed()
    {
        _closing = true;
        _repairTimer.Stop();
        if (_layer is not null) _layer.HealthChanged -= OnHealthChanged;
        _attached = false;
    }
}
