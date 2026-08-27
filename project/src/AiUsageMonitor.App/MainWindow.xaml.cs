using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Platform.Windows.Window;
using Microsoft.Win32;

namespace AiUsageMonitor.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Window disposes the polling timer in OnClosing.")]
public partial class MainWindow : Window
{
    /// <summary>倍率100%時の標準表示の基準論理幅（DIP）。</summary>
    public const double StandardWidgetWidthDip = 280d;
    /// <summary>倍率100%時の縮小表示の基準論理幅（DIP）。標準表示の約半分。</summary>
    public const double CompactWidgetWidthDip = 150d;
    /// <summary>縮小表示の基準論理幅から左右の余白を除いた内容幅（DIP）。</summary>
    public const double CompactWidgetContentWidthDip = 134d;
    public const double CompactWidgetPaddingDip = 8d;
    // 既存のテスト・外部参照に対するソース互換を保つ別名。新規コードはStandardを使う。
    public const double BaseWidgetWidthDip = StandardWidgetWidthDip;

    private bool _userDragging;
    private bool _isClosing;
    private DispatcherOperation? _pendingPlacement;
    private bool _pendingFullApply;
    private DispatcherOperation? _pendingTopmostRecovery;
    private DispatcherTimer? _topmostRecoveryTimer;
    private System.Threading.Timer? _displayTopologyPollTimer;
    private int _displayTopologyPollQueued;
    private MonitorWorkAreaSnapshot? _lastPolledMonitorSnapshot;
    private long _pendingTopmostRequestedTimestamp;
    private bool _hasPendingTopmostRequest;
    private long _lastTopmostNativeStartTimestamp;
    private bool _topmostRecoveryInProgress;
    private bool _appearanceGeometryUpdating;
    private BackgroundPresentationMode _backgroundPresentationMode = BackgroundPresentationMode.Inline;
    private TopmostWindowController? _topmostController;
    private bool _scrollGuidanceShown;
    private int _rootMouseDownCount;
    private readonly DisplayWorkAreaProvider _displayWorkAreaProvider;
    private readonly DisplayReflowScheduler _displayReflowScheduler;
    private HwndSource? _source;
    private bool _systemDisplayEventsSubscribed;
    private WorkArea _lastKnownWorkArea = new(0, 0, 1920, 1040);
    private MonitorWorkAreaSnapshot? _lastMonitorSnapshot;
    public AppSettings Settings { get; private set; } = new();
    public event Action? UserPositionChanged;
    internal event Action? InformationBoundsChanged;
    internal event Action? UserMoveStarted;
    internal event Action? UserMoveCompleted;
    internal event Action? PlacementCompleted;
    public event Action<bool>? TopmostHealthChanged;
    public bool IsTopmostDegraded => _topmostController?.Health.IsDegraded ?? false;
    internal Func<long> TopmostClockForTest { get; set; } = Stopwatch.GetTimestamp;
    public double CurrentBaseWidgetWidthDip => Settings.DisplayMode == WidgetDisplayMode.Compact
        ? CompactWidgetWidthDip
        : StandardWidgetWidthDip;

    public MainWindow()
    {
        _displayWorkAreaProvider = new DisplayWorkAreaProvider();
        _displayReflowScheduler = new DisplayReflowScheduler(
            Dispatcher,
            ExecuteDisplayReflow,
            () => !_isClosing && IsVisible,
            () => _userDragging);
        InitializeComponent();
        AppearanceHost.AddHandler(
            Mouse.MouseDownEvent,
            new MouseButtonEventHandler(OnRootMouseDown),
            handledEventsToo: true);
        AppearanceHost.AddHandler(
            Mouse.MouseUpEvent,
            new MouseButtonEventHandler(OnRootMouseUp),
            handledEventsToo: true);
        ContentRendered += (_, _) => RequestReposition(fullApply: true);
        SizeChanged += (_, _) =>
        {
            UpdateAppearanceGeometry();
            RequestReposition(fullApply: false);
            UpdateScrollGuidance();
        };
        LocationChanged += (_, _) => UpdateMaximumHeight();
        IsVisibleChanged += (_, e) =>
        {
            _displayReflowScheduler.SetVisible(e.NewValue is true);
            if (e.NewValue is true)
            {
                EnsureTopmostControllerEnabled();
                RequestReposition(fullApply: true);
                RequestTopmostRecovery();
            }
        };
    }

    public void ApplySettings(AppSettings settings, bool reposition)
    {
        Settings = settings.Normalized();
        Topmost = Settings.AlwaysOnTop;
        ApplyDisplayMode();
        double scale = Settings.UiScalePercent / 100d;
        // 倍率はWindowではなくRootへ適用する。文字・バー・余白・行高を一括で変形し、
        // Windowの外形幅を論理幅×倍率へ同期する。毎回、基準幅と絶対scaleから再構築するため累積しない。
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        Root.LayoutTransform = transform;
        ApplyAppearanceResources();
        _backgroundPresentationMode = BackgroundPresentationPolicy.Evaluate(Settings, splitAvailable: false);
        AppearanceBrushFactory.Apply(AppearanceHost, BackgroundSurface, Settings);
        UpdateAppearanceGeometry();
        // Opacity remains the legacy whole-window opacity. BackgroundOpacity is
        // encoded only in the background brush above.
        Opacity = Settings.Opacity;
        UpdateMaximumHeight();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateScrollGuidance);
        // Window自身のLayoutTransformは既定のIdentityのまま保つ。
        if (IsLoaded) ApplyClickThrough();
        if (Settings.AlwaysOnTop)
        {
            EnsureTopmostControllerEnabled();
            RequestTopmostRecovery();
        }
        else
        {
            AbortPendingTopmostRecovery();
            _topmostController?.SetEnabled(false);
        }
        if (reposition) RequestReposition(fullApply: true);
    }

    private void ApplyDisplayMode()
    {
        bool compact = Settings.DisplayMode == WidgetDisplayMode.Compact;
        double width = compact ? CompactWidgetWidthDip : StandardWidgetWidthDip;
        Root.Width = width;
        Root.Padding = new Thickness(compact ? CompactWidgetPaddingDip : 10d);
        StandardContentPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactContentPanel.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyAppearanceResources()
    {
        AppSettings settings = Settings.Normalized();
        Resources["MutedBrush"] = CreateBrush(settings.MutedColor);
        Resources["AccentBrush"] = CreateBrush(settings.AccentColor);
        Resources["WarnBrush"] = CreateBrush(settings.WarningColor);
        Resources["DangerBrush"] = CreateBrush(settings.DangerColor);
        if (settings.ForegroundColor is { } foreground)
        {
            Resources["ForegroundBrush"] = CreateBrush(foreground);
            Root.SetResourceReference(TextElement.ForegroundProperty, "ForegroundBrush");
        }
        else
        {
            Resources.Remove("ForegroundBrush");
            Root.ClearValue(TextElement.ForegroundProperty);
        }

        if (settings.FontFamilyName is { } fontFamily)
            Root.SetValue(TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily(fontFamily));
        else
            Root.ClearValue(TextElement.FontFamilyProperty);
    }

    private static SolidColorBrush CreateBrush(string color)
    {
        SolidColorBrush brush = new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void UpdateAppearanceGeometry()
    {
        if (_appearanceGeometryUpdating) return;
        _appearanceGeometryUpdating = true;
        try
        {
            double scale = Settings.UiScalePercent / 100d;
            double ratio = _backgroundPresentationMode != BackgroundPresentationMode.Split
                && Settings.BackgroundEnabled && Settings.BackgroundFillMode == BackgroundFillMode.EdgeFade
                ? Math.Clamp(Settings.BackgroundEdgeFadePercent / 100d, .05, .5)
                : 0;
            double informationWidth = CurrentBaseWidgetWidthDip * scale;
            AppearanceHost.Width = informationWidth * (1d + (2d * ratio));
            Width = AppearanceHost.Width;
            if (ratio > 0 && Root.ActualHeight > 0)
                BackgroundFadeHost.Height = Root.ActualHeight * (1d + (2d * ratio));
            else
                BackgroundFadeHost.ClearValue(HeightProperty);
            InformationBoundsChanged?.Invoke();
        }
        finally
        {
            _appearanceGeometryUpdating = false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThrough();
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WindowProc);
        }
        if (!_systemDisplayEventsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged += OnSystemDisplaySettingsChanged;
            _systemDisplayEventsSubscribed = true;
        }
        StartDisplayTopologyPolling();
        if (handle != 0)
        {
            _topmostController ??= new TopmostWindowController(handle);
            AttachTopmostController(_topmostController);
            _topmostController.SetEnabled(Settings.AlwaysOnTop);
        }
        // レイアウト前で ActualWidth/ActualHeight が0のためここでは配置しない。
        // 実際の配置はレイアウト完了後の ContentRendered から予約経路で1回行う。
        RequestReposition(fullApply: true);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateMaximumHeight();
        UpdateScrollGuidance();
        RequestReposition(fullApply: false);
        _displayReflowScheduler.NotifyDpiChanged();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        StopDisplayTopologyPolling();
        _displayReflowScheduler.Cancel();
        AbortPendingPlacement();
        AbortPendingTopmostRecovery();
        _topmostController?.SetEnabled(false);
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_systemDisplayEventsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= OnSystemDisplaySettingsChanged;
            _systemDisplayEventsSubscribed = false;
        }
        _source?.RemoveHook(WindowProc);
        _source = null;
        _displayReflowScheduler.Shutdown();
        if (_topmostController is not null)
        {
            _topmostController.RecoveryRequested -= RequestTopmostRecovery;
            _topmostController.HealthChanged -= OnTopmostHealthChanged;
        }
        _topmostController?.Dispose();
        _topmostController = null;
        base.OnClosed(e);
    }

    // 予約の破棄は保留フラグも一緒に落とす。full要求済みのまま次のclamp要求が来ると、
    // 残存フラグにより本来clampすべき場面で完全再配置してしまうため。
    private void AbortPendingPlacement()
    {
        _pendingPlacement?.Abort();
        _pendingPlacement = null;
        _pendingFullApply = false;
    }

    private void AbortPendingTopmostRecovery()
    {
        _pendingTopmostRecovery?.Abort();
        _pendingTopmostRecovery = null;
        _topmostRecoveryTimer?.Stop();
        _topmostRecoveryTimer = null;
        _pendingTopmostRequestedTimestamp = 0;
        _hasPendingTopmostRequest = false;
    }

    internal static bool CanRecoverTopmost(
        bool isClosing,
        bool hasHandle,
        bool isVisible,
        bool alwaysOnTop) =>
        !isClosing && hasHandle && isVisible && alwaysOnTop;

    private void EnsureTopmostControllerEnabled()
    {
        if (Settings.AlwaysOnTop)
            _topmostController?.SetEnabled(true);
    }

    private void RequestTopmostRecovery() => RequestTopmostRecovery(TopmostClockForTest());

    private void RequestTopmostRecovery(long requestedAt)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => RequestTopmostRecovery(requestedAt));
            return;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        bool isVisible = TopmostRecoveryVisibilityOverrideForTest ?? IsVisible;
        if (!CanRecoverTopmost(_isClosing, handle != 0, isVisible, Settings.AlwaysOnTop) ||
            _topmostController is null)
        {
            if (!Settings.AlwaysOnTop || _isClosing || !isVisible)
                AbortPendingTopmostRecovery();
            return;
        }
        if (_topmostRecoveryInProgress)
        {
            return;
        }
        if (!_hasPendingTopmostRequest)
        {
            _pendingTopmostRequestedTimestamp = requestedAt;
            _hasPendingTopmostRequest = true;
        }
        else
        {
            _pendingTopmostRequestedTimestamp = Math.Min(_pendingTopmostRequestedTimestamp, requestedAt);
        }
        ScheduleTopmostRecovery();
    }

    private void ScheduleTopmostRecovery()
    {
        if (!_hasPendingTopmostRequest || _pendingTopmostRecovery is not null || _topmostRecoveryTimer is not null)
            return;

        long eligibleAt = Math.Max(
            _pendingTopmostRequestedTimestamp,
            _lastTopmostNativeStartTimestamp + TopmostMinIntervalTicks);
        long now = TopmostClockForTest();
        if (now < eligibleAt)
        {
            _topmostRecoveryTimer = new DispatcherTimer(
                DispatcherPriority.Background,
                Dispatcher)
            {
                Interval = StopwatchTicksToTimeSpan(eligibleAt - now),
            };
            _topmostRecoveryTimer.Tick += OnTopmostRecoveryTimerTick;
            _topmostRecoveryTimer.Start();
            return;
        }

        _pendingTopmostRecovery = Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            ExecuteTopmostRecovery);
    }

    private void OnTopmostRecoveryTimerTick(object? sender, EventArgs e)
    {
        _topmostRecoveryTimer?.Stop();
        _topmostRecoveryTimer = null;
        ScheduleTopmostRecovery();
    }

    private void ExecuteTopmostRecovery()
    {
        _pendingTopmostRecovery = null;
        if (!_hasPendingTopmostRequest) return;
        long requestedAt = _pendingTopmostRequestedTimestamp;
        _pendingTopmostRequestedTimestamp = 0;
        _hasPendingTopmostRequest = false;

        bool effectiveVisible = TopmostRecoveryVisibilityOverrideForTest ?? IsVisible;
        nint currentHandle = new WindowInteropHelper(this).Handle;
        if (!CanRecoverTopmost(_isClosing, currentHandle != 0, effectiveVisible, Settings.AlwaysOnTop))
            return;

        long now = TopmostClockForTest();
        long eligibleAt = Math.Max(requestedAt, _lastTopmostNativeStartTimestamp + TopmostMinIntervalTicks);
        if (now < eligibleAt)
        {
            _pendingTopmostRequestedTimestamp = requestedAt;
            _hasPendingTopmostRequest = true;
            ScheduleTopmostRecovery();
            return;
        }

        _lastTopmostNativeStartTimestamp = now;
        _topmostRecoveryInProgress = true;
        try
        {
            _topmostController?.TryRecover();
        }
        catch
        {
            // A transient user32 failure is retried by the next normal WinEvent.
        }
        finally
        {
            _topmostRecoveryInProgress = false;
        }
    }

    private static readonly long TopmostMinIntervalTicks =
        (Stopwatch.Frequency * 250L) / 1000L;

    private static TimeSpan StopwatchTicksToTimeSpan(long ticks) =>
        TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

    private void AttachTopmostController(TopmostWindowController controller)
    {
        controller.RecoveryRequested -= RequestTopmostRecovery;
        controller.RecoveryRequested += RequestTopmostRecovery;
        controller.HealthChanged -= OnTopmostHealthChanged;
        controller.HealthChanged += OnTopmostHealthChanged;
        OnTopmostHealthChanged(controller.Health);
    }

    private void OnTopmostHealthChanged(TopmostHealth health) =>
        TopmostHealthChanged?.Invoke(health.IsDegraded);

    /// <summary>
    /// 配置更新をDispatcher上で1件だけ予約する。ドラッグ中・終了処理中・未ロード・非表示では予約しない。
    /// fullApply=true はプリセット/自由配置の完全再適用、false は SizeChanged 由来（自由配置はclampのみ）。
    /// 予約待ち中に fullApply が要求されたら、実行時の適用を完全再適用へ引き上げる。
    /// </summary>
    private void RequestReposition(bool fullApply)
    {
        if (_isClosing || !IsLoaded || _userDragging || !IsVisible) return;
        if (fullApply) _pendingFullApply = true;
        if (_pendingPlacement is not null) return;
        _pendingPlacement = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            bool full = _pendingFullApply;
            _pendingPlacement = null;
            _pendingFullApply = false;
            // DragMove のモーダル移動ループはメッセージをポンプするため、予約が
            // ドラッグ中に実行開始しうる。Abort で止められない経路をここで弾く。
            if (_isClosing || _userDragging || !IsVisible) return;
            UpdateLayout();
            UpdateAppearanceGeometry();
            bool positioned = !full && Settings.PlacementMode == PlacementMode.Custom
                ? ClampCurrentPosition()
                : PositionFromSettings();
            if (!positioned) return;
            if (_lastMonitorSnapshot is { } snapshot)
            {
                DisplayReflowEventSource.Log.PlacementConfirmed(
                    _displayReflowScheduler.ActiveWaveId,
                    Stopwatch.GetTimestamp(),
                    _displayReflowScheduler.ActiveWaveIsRetry,
                    snapshot.DeviceName,
                    snapshot.WorkingArea.Left,
                    snapshot.WorkingArea.Top,
                    snapshot.WorkingArea.Width,
                    snapshot.WorkingArea.Height);
            }
            PlacementCompleted?.Invoke();
        });
    }

    private bool ExecuteDisplayReflow()
    {
        if (_isClosing || !IsLoaded || !IsVisible || _userDragging)
            return false;
        if (!TryRefreshWorkArea(out _, out MonitorResolutionResult resolution))
        {
            DisplayReflowEventSource.Log.Failure(resolution.FailureStage ?? "monitor-resolution");
            return false;
        }

        UpdateMaximumHeight();
        UpdateScrollGuidance();
        RequestReposition(fullApply: true);
        return true;
    }

    private bool PositionFromSettings()
    {
        if (!TryRefreshWorkArea(out DisplayWorkArea workArea, out MonitorResolutionResult resolution))
        {
            DisplayReflowEventSource.Log.Failure(resolution.FailureStage ?? "monitor-resolution");
            return false;
        }

        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(
            workArea.LocalDipArea,
            InformationWidthDip(),
            InformationHeightDip(),
            Settings);
        (double insetX, double insetY) = InformationInsetsDip();
        PhysicalWindowPosition position = DisplayWorkAreaProvider.ToPhysicalWindowPosition(
            workArea.Snapshot,
            placement.Left,
            placement.Top,
            insetX,
            insetY);
        if (!TrySetPhysicalPosition(position, "SetWindowPos-position"))
            return false;

        // LayoutTransform、透明edge fade、native DPI丸めにより、予測した情報領域と
        // SetWindowPos後の実HWND矩形には差が生じうる。回転後の狭い辺ではその差が
        // 画面外へのはみ出しになるため、実矩形を読み直して物理pixelで最終clampする。
        return ClampCurrentPosition();
    }

    private bool ClampCurrentPosition()
    {
        if (!TryRefreshWorkArea(out DisplayWorkArea workArea, out MonitorResolutionResult resolution))
        {
            DisplayReflowEventSource.Log.Failure(resolution.FailureStage ?? "monitor-resolution");
            return false;
        }

        nint handle = new WindowInteropHelper(this).Handle;
        int errorCode = 0;
        if (handle == 0 || !NativeWindowPositioner.TryGetBounds(handle, out MonitorPixelRect outerBounds, out errorCode))
        {
            LogNativePositionFailure("GetWindowRect-clamp", errorCode);
            return false;
        }

        MonitorPixelRect clamped = DisplayWorkAreaProvider.ClampWindowBounds(
            workArea.Snapshot,
            outerBounds);
        var position = new PhysicalWindowPosition(clamped.Left, clamped.Top);
        return TrySetPhysicalPosition(position, "SetWindowPos-clamp");
    }

    private WorkArea CurrentWorkArea()
    {
        return TryRefreshWorkArea(out DisplayWorkArea workArea, out _)
            ? workArea.LocalDipArea
            : _lastKnownWorkArea;
    }

    private bool TryRefreshWorkArea(
        out DisplayWorkArea workArea,
        out MonitorResolutionResult resolution,
        bool forDrag = false)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        (int X, int Y) center;
        if (handle != 0 && NativeWindowPositioner.TryGetBounds(handle, out MonitorPixelRect bounds, out _))
        {
            center = (
                bounds.Left + (bounds.Width / 2),
                bounds.Top + (bounds.Height / 2));
        }
        else
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            center = (
                (int)Math.Round((Left + (ActualWidth / 2d)) * dpi.DpiScaleX),
                (int)Math.Round((Top + (ActualHeight / 2d)) * dpi.DpiScaleY));
        }
        bool success = forDrag
            ? _displayWorkAreaProvider.TryGetForDrag(handle, center, out workArea, out resolution)
            : _displayWorkAreaProvider.TryGetCurrent(
                Settings.MonitorDeviceName,
                handle,
                center,
                out workArea,
                out resolution);
        if (success)
        {
            _lastKnownWorkArea = workArea.LocalDipArea;
            _lastMonitorSnapshot = workArea.Snapshot;
        }
        return success;
    }

    private void UpdateMaximumHeight()
    {
        WorkArea area = CurrentWorkArea();
        Root.MaxHeight = AppearanceGeometryCalculator.CalculateMaximumInformationHeight(
            area.Height,
            Settings.VerticalMarginDip,
            Settings.UiScalePercent);
    }

    public AppSettings CaptureCustomPosition()
    {
        if (!TryRefreshWorkArea(out DisplayWorkArea workArea, out _, forDrag: true)) return Settings;
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle == 0 || !NativeWindowPositioner.TryGetBounds(handle, out MonitorPixelRect outerBounds, out _))
            return Settings;
        (double insetX, double insetY) = InformationInsetsDip();
        MonitorPixelRect informationPixels = DisplayWorkAreaProvider.GetInformationBounds(
            workArea.Snapshot,
            outerBounds,
            insetX,
            insetY,
            InformationWidthDip(),
            InformationHeightDip());
        if (!DisplayWorkAreaProvider.TryCaptureCustomPosition(
                workArea.Snapshot,
                informationPixels,
                out CapturedDisplayPosition captured))
            return Settings;
        return Settings with
        {
            MonitorDeviceName = captured.DeviceName,
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = captured.LeftFraction,
            CustomTopFraction = captured.TopFraction,
        };
    }

    private double InformationWidthDip() => CurrentBaseWidgetWidthDip * (Settings.UiScalePercent / 100d);

    private double InformationHeightDip() => Root.ActualHeight > 0 ? Root.ActualHeight : ActualHeight;

    private (double X, double Y) InformationInsetsDip() =>
        (
            Math.Max(0, (ActualWidth - InformationWidthDip()) / 2d),
            Math.Max(0, (ActualHeight - InformationHeightDip()) / 2d));

    private (double Left, double Top) InformationPosition()
    {
        double offsetX = Math.Max(0, (ActualWidth - InformationWidthDip()) / 2d);
        double offsetY = Math.Max(0, (ActualHeight - InformationHeightDip()) / 2d);
        return (Left + offsetX, Top + offsetY);
    }

    private bool TrySetPhysicalPosition(PhysicalWindowPosition position, string failureStage)
    {
        nint handle = new WindowInteropHelper(this).Handle;
        int errorCode = 0;
        if (handle != 0 && NativeWindowPositioner.TrySetPosition(
                handle,
                position.Left,
                position.Top,
                out errorCode))
            return true;

        LogNativePositionFailure(failureStage, errorCode);
        return false;
    }

    private void LogNativePositionFailure(string stage, int errorCode) =>
        DisplayReflowEventSource.Log.InteropFailure(
            _displayReflowScheduler.ActiveWaveId,
            Stopwatch.GetTimestamp(),
            _displayReflowScheduler.ActiveWaveIsRetry,
            stage,
            errorCode == 0 ? 0 : unchecked((int)(0x80070000u | (uint)errorCode)));

    private void ApplyClickThrough()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) ClickThroughHelper.Apply(handle, Settings.ClickThrough);
        UpdateScrollGuidance();
    }

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        Interlocked.Increment(ref _rootMouseDownCount);
        if (!Settings.ClickThrough && e.ButtonState == MouseButtonState.Pressed)
        {
            _userDragging = true;
            _displayReflowScheduler.SetDragging(true);
            UserMoveStarted?.Invoke();
            // ドラッグ開始前に積まれていた配置予約を破棄する。実行済みならコールバック側の再判定で弾く。
            AbortPendingPlacement();
            DragMove();
            CompleteDrag();
        }
    }

    private void OnRootMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && _userDragging)
            CompleteDrag();
    }

    private void OnContentScrollChanged(
        object sender,
        System.Windows.Controls.ScrollChangedEventArgs e) =>
        UpdateScrollGuidance();

    private void UpdateScrollGuidance()
    {
        if (!IsInitialized)
            return;
        bool scrollRequired = ContentScrollViewer.ExtentHeight >
            ContentScrollViewer.ViewportHeight + 0.5;
        bool shouldShow = Settings.ClickThrough &&
            scrollRequired &&
            !_scrollGuidanceShown;
        if (shouldShow)
        {
            _scrollGuidanceShown = true;
            ScrollGuidanceText.Visibility = Visibility.Visible;
        }
        else if (!Settings.ClickThrough || !scrollRequired)
        {
            ScrollGuidanceText.Visibility = Visibility.Collapsed;
        }
    }

    // ドラッグ完了時、自身のSettingsを自由配置(Custom)へ更新してから位置変更を通知する。
    // これを怠るとPlacementModeがPresetのまま残り、次のSizeChangedが四隅へ戻してしまう。
    private void CompleteDrag()
    {
        _userDragging = false;
        Settings = CaptureCustomPosition();
        UserMoveCompleted?.Invoke();
        UserPositionChanged?.Invoke();
        _displayReflowScheduler.SetDragging(false);
    }

    internal void SetInlineBackground(AppSettings settings, BackgroundPresentationMode mode)
    {
        Settings = settings.Normalized();
        _backgroundPresentationMode = mode;
        if (mode == BackgroundPresentationMode.None)
            AppearanceBrushFactory.Clear(AppearanceHost, BackgroundSurface);
        else if (mode == BackgroundPresentationMode.Inline)
            AppearanceBrushFactory.Apply(AppearanceHost, BackgroundSurface, Settings);
        else
            AppearanceBrushFactory.Clear(AppearanceHost, BackgroundSurface);
        UpdateAppearanceGeometry();
    }

    internal Rect GetInformationBoundsInScreenDip()
    {
        (double left, double top) = InformationPosition();
        return new(left, top, InformationWidthDip(), InformationHeightDip());
    }

    // --- テスト用フック（InternalsVisibleTo 経由） ---
    internal bool HasPendingPlacementForTest => _pendingPlacement is not null;

    internal bool PendingFullApplyForTest => _pendingFullApply;

    internal bool DraggingForTest { get => _userDragging; set => _userDragging = value; }
    internal int RootMouseDownCountForTest => Volatile.Read(ref _rootMouseDownCount);
    internal bool HasPendingTopmostForTest => _hasPendingTopmostRequest || _pendingTopmostRecovery is not null;
    internal bool? TopmostRecoveryVisibilityOverrideForTest { get; set; }

    internal void RequestRepositionForTest(bool fullApply) => RequestReposition(fullApply);
    internal void NotifyDisplayChangeForTest() => _displayReflowScheduler.NotifyDisplayChange();
    internal bool HasPendingDisplayReflowForTest => _displayReflowScheduler.HasPendingWork;
    internal void RepositionAfterContentChange()
    {
        UpdateMaximumHeight();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateScrollGuidance);
        RequestReposition(fullApply: false);
    }

    internal void CompleteDragForTest() => CompleteDrag();

    internal void DrainPendingPlacementForTest() =>
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    internal void RequestTopmostRecoveryForTest() => RequestTopmostRecovery();

    internal void DrainPendingTopmostForTest() =>
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    internal void SetTopmostControllerForTest(TopmostWindowController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        if (_topmostController is not null)
        {
            _topmostController.RecoveryRequested -= RequestTopmostRecovery;
            _topmostController.HealthChanged -= OnTopmostHealthChanged;
        }
        _topmostController = controller;
        AttachTopmostController(_topmostController);
    }

    // ドラッグ開始時に本番が行う「保留予約の破棄」だけを切り出したもの。DragMoveのモーダルループは実行できないため。
    internal void SimulateDragAbortPendingPlacement() => AbortPendingPlacement();

    private nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (DisplayReflowMessageFilter.IsDisplayOrWorkAreaChange(message, wParam))
            _displayReflowScheduler.NotifyDisplayChange();
        return 0;
    }

    private void OnSystemDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_isClosing) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_isClosing)
                _displayReflowScheduler.NotifyDisplayChange();
        });
    }

    internal void NotifySystemDisplaySettingsChangedForTest() =>
        OnSystemDisplaySettingsChanged(null, EventArgs.Empty);

    private void StartDisplayTopologyPolling()
    {
        if (_displayTopologyPollTimer is not null) return;
        _displayTopologyPollTimer = new System.Threading.Timer(
            _ => QueueDisplayTopologyPoll(),
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    private void StopDisplayTopologyPolling()
    {
        Interlocked.Exchange(ref _displayTopologyPollTimer, null)?.Dispose();
        _lastPolledMonitorSnapshot = null;
    }

    private void QueueDisplayTopologyPoll()
    {
        if (Interlocked.Exchange(ref _displayTopologyPollQueued, 1) != 0) return;
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                try
                {
                    OnDisplayTopologyPoll();
                }
                finally
                {
                    Volatile.Write(ref _displayTopologyPollQueued, 0);
                }
            });
        }
        catch
        {
            Volatile.Write(ref _displayTopologyPollQueued, 0);
        }
    }

    private void OnDisplayTopologyPoll()
    {
        if (_displayTopologyPollTimer is null || _isClosing || !IsVisible || _userDragging) return;
        MonitorWorkAreaSnapshot? previous = _lastPolledMonitorSnapshot;
        if (!TryRefreshWorkArea(out DisplayWorkArea current, out _)) return;
        _lastPolledMonitorSnapshot = current.Snapshot;
        nint handle = new WindowInteropHelper(this).Handle;
        bool outside = handle != 0 &&
            NativeWindowPositioner.TryGetBounds(handle, out MonitorPixelRect bounds, out _) &&
            DisplayWorkAreaProvider.IsOutsideWorkArea(current.Snapshot, bounds);
        if (outside || DisplayWorkAreaProvider.HasTopologyChanged(previous, current.Snapshot))
            RequestReposition(fullApply: true);
    }
}
