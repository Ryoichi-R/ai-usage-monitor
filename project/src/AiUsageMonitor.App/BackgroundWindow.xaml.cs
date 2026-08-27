using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Platform.Windows.Window;

namespace AiUsageMonitor.App;

public partial class BackgroundWindow : Window
{
    private readonly IWindowLayerApi _layerApi;
    private readonly WindowLayerRepairEngine _repair;
    private readonly DispatcherTimer _repairTimer;
    private readonly Action<string, Exception?> _diagnostic;
    private HwndSource? _source;
    private nint _handle;
    private AppSettings _settings = new AppSettings().Normalized();
    private bool _requested;
    private bool _suspended;
    private bool _suppressed;
    private bool _closing;

    internal BackgroundWindow(Action<string, Exception?> diagnostic, IWindowLayerApi? layerApi = null)
    {
        _diagnostic = diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
        _layerApi = layerApi ?? NativeWindowLayerApi.Instance;
        _repair = new WindowLayerRepairEngine(_layerApi);
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        _repairTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => RunRepairTick(), Dispatcher);
        _repairTimer.Stop();
    }

    internal nint Handle => _handle;
    internal bool LayerFailureSuppressed => _suppressed;

    internal void ApplyAppearance(AppSettings settings)
    {
        _settings = settings.Normalized();
        AppearanceBrushFactory.Apply(BackgroundFadeHost, BackgroundRoot, _settings);
        Opacity = _settings.Opacity;
    }

    internal void SetPresentationRequested(bool requested)
    {
        Dispatcher.VerifyAccess();
        _requested = requested;
        if (!requested) { Hide(); return; }
        if (!IsVisible) Show();
        ReapplyBottomMost(forceApply: true, countFailure: false);
    }

    internal void SetRepairSuspended(bool suspended)
    {
        Dispatcher.VerifyAccess();
        _suspended = suspended;
        if (!suspended && _requested && IsVisible) ReapplyBottomMost(forceApply: true, countFailure: false);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        if (_handle == 0) return;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowProc);
        try { ClickThroughHelper.Apply(_handle, true); }
        catch (Exception exception) { _diagnostic("background-layer-style-failure", exception); }
        ReapplyBottomMost(forceApply: true, countFailure: false);
        _repairTimer.Start();
    }

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == BottomMostStrategy.TaskbarCreatedMessage || message == 0x007E)
            ReapplyBottomMost(forceApply: true, countFailure: false);
        else if (message == 0x0046 && !_suspended)
        {
            try { _ = BottomMostStrategy.RewriteWindowPosForLayer(lParam, LayerStrategy.BottomMost, false, _layerApi); }
            catch (Exception exception) { _diagnostic("background-layer-window-position-failure", exception); }
        }
        return 0;
    }

    private void RunRepairTick() => ReapplyBottomMost(forceApply: false, countFailure: true);

    private void ReapplyBottomMost(bool forceApply, bool countFailure)
    {
        if (_closing || !_requested || _suspended || _handle == 0) return;
        LayerRepairResult result = _repair.Repair(
            _handle,
            LayerStrategy.BottomMost,
            LayerRepairTrigger.Timer,
            countFailure,
            forceApply);
        if (result.Outcome == LayerRepairOutcome.FallbackPending)
        {
            if (!_suppressed)
            {
                _suppressed = true;
                Opacity = 0;
                _diagnostic("background-layer-suppressed", null);
            }
        }
        else if (result.Outcome == LayerRepairOutcome.Healthy && _suppressed)
        {
            _suppressed = false;
            Opacity = _settings.Opacity;
            _diagnostic("background-layer-recovered", null);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closing = true;
        _repairTimer.Stop();
        _source?.RemoveHook(WindowProc);
        _source = null;
        _handle = 0;
        _repair.ResetEpisode();
    }
}
