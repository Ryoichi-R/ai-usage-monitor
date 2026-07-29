using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Windows.Window;
using Forms = System.Windows.Forms;

namespace AiUsageMonitor.App;

public partial class MainWindow : Window
{
    /// <summary>倍率100%時のメインウィジェットの基準論理幅（DIP）。倍率変更はこの幅を不変に保つ。</summary>
    public const double BaseWidgetWidthDip = 280d;

    private bool _userDragging;
    private bool _isClosing;
    private DispatcherOperation? _pendingPlacement;
    private bool _pendingFullApply;
    private bool _scrollGuidanceShown;
    private int _rootMouseDownCount;
    public AppSettings Settings { get; private set; } = new();
    public event Action? UserPositionChanged;

    public MainWindow()
    {
        InitializeComponent();
        Root.AddHandler(
            Mouse.MouseDownEvent,
            new MouseButtonEventHandler(OnRootMouseDown),
            handledEventsToo: true);
        Root.AddHandler(
            Mouse.MouseUpEvent,
            new MouseButtonEventHandler(OnRootMouseUp),
            handledEventsToo: true);
        ContentRendered += (_, _) => RequestReposition(fullApply: true);
        SizeChanged += (_, _) =>
        {
            RequestReposition(fullApply: false);
            UpdateScrollGuidance();
        };
        LocationChanged += (_, _) => UpdateMaximumHeight();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) RequestReposition(fullApply: true); };
    }

    public void ApplySettings(AppSettings settings, bool reposition)
    {
        Settings = settings.Normalized();
        Topmost = Settings.AlwaysOnTop;
        Opacity = Settings.Opacity;
        double scale = Settings.UiScalePercent / 100d;
        // 倍率はWindowではなくRootへ適用する。文字・バー・余白・行高を一括で変形し、
        // Windowの外形幅を論理幅×倍率へ同期する。毎回、基準幅と絶対scaleから再構築するため累積しない。
        Root.Width = BaseWidgetWidthDip;
        var transform = new ScaleTransform(scale, scale);
        transform.Freeze();
        Root.LayoutTransform = transform;
        Width = BaseWidgetWidthDip * scale;
        UpdateMaximumHeight();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateScrollGuidance);
        // Window自身のLayoutTransformは既定のIdentityのまま保つ。
        if (IsLoaded) ApplyClickThrough();
        if (reposition) RequestReposition(fullApply: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyClickThrough();
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
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _isClosing = true;
        AbortPendingPlacement();
        base.OnClosing(e);
    }

    // 予約の破棄は保留フラグも一緒に落とす。full要求済みのまま次のclamp要求が来ると、
    // 残存フラグにより本来clampすべき場面で完全再配置してしまうため。
    private void AbortPendingPlacement()
    {
        _pendingPlacement?.Abort();
        _pendingPlacement = null;
        _pendingFullApply = false;
    }

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
            if (!full && Settings.PlacementMode == PlacementMode.Custom) ClampCurrentPosition();
            else PositionFromSettings();
        });
    }

    private void PositionFromSettings()
    {
        WorkArea area = CurrentWorkArea();
        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(area, ActualWidth, ActualHeight, Settings);
        Left = placement.Left;
        Top = placement.Top;
    }

    private void ClampCurrentPosition()
    {
        WorkArea area = CurrentWorkArea();
        WidgetPlacement placement = WidgetPlacementCalculator.ClampToArea(area, ActualWidth, ActualHeight, Left, Top);
        Left = placement.Left;
        Top = placement.Top;
    }

    private WorkArea CurrentWorkArea()
    {
        Forms.Screen screen = FindScreen(Settings.MonitorDeviceName);
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        return new WorkArea(
            screen.WorkingArea.Left / dpi.DpiScaleX,
            screen.WorkingArea.Top / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX,
            screen.WorkingArea.Height / dpi.DpiScaleY);
    }

    private void UpdateMaximumHeight()
    {
        WorkArea area = CurrentWorkArea();
        double scale = Settings.UiScalePercent / 100d;
        Root.MaxHeight = Math.Max(
            0,
            (area.Height - (2 * Settings.VerticalMarginDip)) / scale);
    }

    public AppSettings CaptureCustomPosition()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        Forms.Screen screen = Forms.Screen.FromPoint(new System.Drawing.Point((int)((Left + (ActualWidth / 2)) * dpi.DpiScaleX), (int)((Top + (ActualHeight / 2)) * dpi.DpiScaleY)));
        System.Windows.Rect area = new(screen.WorkingArea.Left / dpi.DpiScaleX, screen.WorkingArea.Top / dpi.DpiScaleY, screen.WorkingArea.Width / dpi.DpiScaleX, screen.WorkingArea.Height / dpi.DpiScaleY);
        double left = Math.Max(0, area.Width - ActualWidth) == 0 ? 0 : (Left - area.Left) / (area.Width - ActualWidth);
        double top = Math.Max(0, area.Height - ActualHeight) == 0 ? 0 : (Top - area.Top) / (area.Height - ActualHeight);
        return Settings with { MonitorDeviceName = screen.DeviceName, PlacementMode = PlacementMode.Custom, CustomLeftFraction = Math.Clamp(left, 0, 1), CustomTopFraction = Math.Clamp(top, 0, 1) };
    }

    private void ApplyClickThrough()
    {
        nint handle = new WindowInteropHelper(this).Handle;
        if (handle != 0) ClickThroughHelper.Apply(handle, Settings.ClickThrough);
        UpdateScrollGuidance();
    }

    private static Forms.Screen FindScreen(string? deviceName) => Forms.Screen.AllScreens.FirstOrDefault(screen => string.Equals(screen.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];

    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;
        Interlocked.Increment(ref _rootMouseDownCount);
        if (!Settings.ClickThrough && e.ButtonState == MouseButtonState.Pressed)
        {
            _userDragging = true;
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
        UserPositionChanged?.Invoke();
    }

    // --- テスト用フック（InternalsVisibleTo 経由） ---
    internal bool HasPendingPlacementForTest => _pendingPlacement is not null;

    internal bool PendingFullApplyForTest => _pendingFullApply;

    internal bool DraggingForTest { get => _userDragging; set => _userDragging = value; }
    internal int RootMouseDownCountForTest => Volatile.Read(ref _rootMouseDownCount);

    internal void RequestRepositionForTest(bool fullApply) => RequestReposition(fullApply);
    internal void RepositionAfterContentChange()
    {
        UpdateMaximumHeight();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateScrollGuidance);
        RequestReposition(fullApply: false);
    }

    internal void CompleteDragForTest() => CompleteDrag();

    internal void DrainPendingPlacementForTest() =>
        Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

    // ドラッグ開始時に本番が行う「保留予約の破棄」だけを切り出したもの。DragMoveのモーダルループは実行できないため。
    internal void SimulateDragAbortPendingPlacement() => AbortPendingPlacement();
}
