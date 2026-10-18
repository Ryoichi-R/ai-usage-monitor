using AiUsageMonitor.App.UI.Appearance;
using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;

namespace AiUsageMonitor.App.UI.Views;

/// <summary>
/// Phase 2のAvalonia版メインウィジェット。WPF版<c>MainWindow</c>の表示部分（表示モード、倍率、
/// 外観リソース、背景の外形、スクロール案内）を移植したもの。
/// 位置決め・topmost回復・クリック透過・ディスプレイ変更追従はOS固有であり、
/// <see cref="RepositionRequested"/>などを通してPlatform層のhostが担う。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>倍率100%時の標準表示の基準論理幅（DIP）。</summary>
    public const double StandardWidgetWidthDip = 280d;
    /// <summary>倍率100%時の縮小表示の基準論理幅（DIP）。標準表示の約半分。</summary>
    public const double CompactWidgetWidthDip = 150d;
    /// <summary>縮小表示の基準論理幅から左右の余白を除いた内容幅（DIP）。</summary>
    public const double CompactWidgetContentWidthDip = 134d;
    public const double CompactWidgetPaddingDip = 8d;
    public const double StandardWidgetPaddingDip = 10d;
    /// <summary>作業領域をhostから受け取るまでの既定高さ。WPF版の初期作業領域と同じ値。</summary>
    public const double DefaultWorkAreaHeightDip = 1040d;

    private bool _appearanceGeometryUpdating;
    private BackgroundPresentationMode _backgroundPresentationMode = BackgroundPresentationMode.Inline;
    private bool _scrollGuidanceShown;
    private double _workAreaHeightDip = DefaultWorkAreaHeightDip;
    private readonly DispatcherTimer _moveSettleTimer;

    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) =>
        {
            UpdateAppearanceGeometry();
            RequestReposition(fullApply: false);
            UpdateScrollGuidance();
        };
        RootScale.SizeChanged += (_, _) => UpdateAppearanceGeometry();
        ContentScrollViewer.ScrollChanged += (_, _) => UpdateScrollGuidance();
        // ドラッグ移動の終了はOSによって通知経路が異なる（macOSはwindow drag中にPointerReleasedが届かない場合がある）。
        // 位置変化が一定時間止まった時点も終了として扱う。
        _moveSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(MoveSettleMilliseconds) };
        _moveSettleTimer.Tick += (_, _) => CompleteUserMove();
        Root.PointerPressed += OnRootPointerPressed;
        Root.PointerReleased += (_, _) => CompleteUserMove();
        PositionChanged += (_, _) =>
        {
            if (!IsUserMoving) return;
            _moveSettleTimer.Stop();
            _moveSettleTimer.Start();
        };
    }

    internal const int MoveSettleMilliseconds = 400;

    public AppSettings Settings { get; private set; } = new();

    /// <summary>配置の再評価が必要になったことをhostへ伝える。引数はプリセット／自由配置の完全再適用かどうか。</summary>
    public event Action<bool>? RepositionRequested;

    /// <summary>情報領域（背景fadeを除く表示部分）の大きさや位置が変わった。分離背景の追従に使う。</summary>
    public event Action? InformationBoundsChanged;

    /// <summary>利用者がウィジェットのドラッグ移動を始めた。hostは移動中の再配置を控える。</summary>
    public event Action? UserMoveStarted;

    /// <summary>ドラッグ移動が終わった。hostは現在位置を自由配置として保存する。</summary>
    public event Action? UserMoveCompleted;

    /// <summary>ドラッグ移動中かどうか。</summary>
    public bool IsUserMoving { get; private set; }

    public double CurrentBaseWidgetWidthDip => Settings.DisplayMode == WidgetDisplayMode.Compact
        ? CompactWidgetWidthDip
        : StandardWidgetWidthDip;

    internal bool IsScrollGuidanceVisible => ScrollGuidanceText.IsVisible;

    public void ApplySettings(AppSettings settings, bool reposition)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings.Normalized();
        ApplyDisplayMode();
        // 倍率はWindowではなくRootを包むLayoutTransformControlへ適用する。文字・バー・余白・行高を
        // 一括で変形し、Windowの外形幅を論理幅×倍率へ同期する。毎回、絶対値から作り直すため累積しない。
        double scale = Settings.UiScalePercent / 100d;
        RootScale.LayoutTransform = new ScaleTransform(scale, scale);
        ApplyAppearanceResources();
        _backgroundPresentationMode = BackgroundPresentationPolicy.Evaluate(Settings, splitAvailable: false);
        AvaloniaAppearanceBrushes.Apply(AppearanceHost, BackgroundSurface, Settings);
        UpdateAppearanceGeometry();
        // Opacityは従来どおりウィンドウ全体の不透明度。背景の不透明度はbrushへ含める。
        Opacity = Settings.Opacity;
        UpdateMaximumHeight();
        Dispatcher.UIThread.Post(UpdateScrollGuidance, DispatcherPriority.Loaded);
        if (reposition) RequestReposition(fullApply: true);
    }

    /// <summary>hostが解決した作業領域の高さ（DIP）を反映し、表示できる最大高を更新する。</summary>
    public void SetWorkAreaHeight(double workAreaHeightDip)
    {
        if (!double.IsFinite(workAreaHeightDip) || workAreaHeightDip <= 0) return;
        _workAreaHeightDip = workAreaHeightDip;
        UpdateMaximumHeight();
        UpdateScrollGuidance();
    }

    internal void SetInlineBackground(AppSettings settings, BackgroundPresentationMode mode)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings.Normalized();
        _backgroundPresentationMode = mode;
        if (mode == BackgroundPresentationMode.Inline)
            AvaloniaAppearanceBrushes.Apply(AppearanceHost, BackgroundSurface, Settings);
        else
            AvaloniaAppearanceBrushes.Clear(AppearanceHost, BackgroundSurface);
        UpdateAppearanceGeometry();
    }

    /// <summary>情報領域の画面上の位置と大きさ（DIP）。</summary>
    public Rect GetInformationBoundsInScreenDip()
    {
        double scaling = RenderScaling > 0 ? RenderScaling : 1d;
        (double insetX, double insetY) = InformationInsetsDip();
        return new(
            (Position.X / scaling) + insetX,
            (Position.Y / scaling) + insetY,
            InformationWidthDip(),
            InformationHeightDip());
    }

    /// <summary>配置計算に使う情報領域の大きさと、ウィンドウ外形からの内側オフセット（DIP）。</summary>
    public InformationGeometry GetInformationGeometry()
    {
        (double insetX, double insetY) = InformationInsetsDip();
        return new(InformationWidthDip(), InformationHeightDip(), insetX, insetY);
    }

    /// <summary>ドラッグ移動を開始する。クリック透過中は移動しない（WPF版と同じ）。</summary>
    internal bool TryBeginUserMove()
    {
        if (Settings.ClickThrough) return false;
        IsUserMoving = true;
        UserMoveStarted?.Invoke();
        return true;
    }

    /// <summary>ドラッグ移動を終える。重複して呼ばれても完了通知は1回だけ行う。</summary>
    internal void CompleteUserMove()
    {
        _moveSettleTimer.Stop();
        if (!IsUserMoving) return;
        IsUserMoving = false;
        UserMoveCompleted?.Invoke();
    }

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || !TryBeginUserMove()) return;
        BeginMoveDrag(e);
        _moveSettleTimer.Stop();
        _moveSettleTimer.Start();
    }

    public void RepositionAfterContentChange()
    {
        UpdateMaximumHeight();
        Dispatcher.UIThread.Post(UpdateScrollGuidance, DispatcherPriority.Loaded);
        RequestReposition(fullApply: false);
    }

    private void RequestReposition(bool fullApply) => RepositionRequested?.Invoke(fullApply);

    private void ApplyDisplayMode()
    {
        bool compact = Settings.DisplayMode == WidgetDisplayMode.Compact;
        Root.Width = compact ? CompactWidgetWidthDip : StandardWidgetWidthDip;
        Root.Padding = new Thickness(compact ? CompactWidgetPaddingDip : StandardWidgetPaddingDip);
        StandardContentPanel.IsVisible = !compact;
        CompactContentPanel.IsVisible = compact;
    }

    private void ApplyAppearanceResources()
    {
        AppSettings settings = Settings;
        Resources["MutedBrush"] = CreateBrush(settings.MutedColor);
        Resources["AccentBrush"] = CreateBrush(settings.AccentColor);
        Resources["WarnBrush"] = CreateBrush(settings.WarningColor);
        Resources["DangerBrush"] = CreateBrush(settings.DangerColor);
        if (settings.ForegroundColor is { } foreground)
            Root.SetValue(TextElement.ForegroundProperty, CreateBrush(foreground));
        else
            Root.ClearValue(TextElement.ForegroundProperty);

        if (settings.FontFamilyName is { } fontFamily)
            Root.SetValue(TextElement.FontFamilyProperty, new FontFamily(fontFamily));
        else
            Root.ClearValue(TextElement.FontFamilyProperty);
    }

    private static ImmutableSolidColorBrush CreateBrush(string color) => new(Color.Parse(color));

    private void UpdateAppearanceGeometry()
    {
        if (_appearanceGeometryUpdating) return;
        _appearanceGeometryUpdating = true;
        try
        {
            double ratio = _backgroundPresentationMode != BackgroundPresentationMode.Split
                && Settings.BackgroundEnabled && Settings.BackgroundFillMode == BackgroundFillMode.EdgeFade
                ? Math.Clamp(Settings.BackgroundEdgeFadePercent / 100d, .05, .5)
                : 0;
            double width = InformationWidthDip() * (1d + (2d * ratio));
            AppearanceHost.Width = width;
            Width = width;
            double informationHeight = RootScale.Bounds.Height;
            if (ratio > 0 && informationHeight > 0)
                BackgroundFadeHost.Height = informationHeight * (1d + (2d * ratio));
            else
                BackgroundFadeHost.ClearValue(HeightProperty);
            InformationBoundsChanged?.Invoke();
        }
        finally
        {
            _appearanceGeometryUpdating = false;
        }
    }

    private void UpdateMaximumHeight()
    {
        Root.MaxHeight = AppearanceGeometryCalculator.CalculateMaximumInformationHeight(
            _workAreaHeightDip,
            Settings.VerticalMarginDip,
            Settings.UiScalePercent);
    }

    private void UpdateScrollGuidance()
    {
        bool scrollRequired = ContentScrollViewer.Extent.Height >
            ContentScrollViewer.Viewport.Height + 0.5;
        bool shouldShow = Settings.ClickThrough &&
            scrollRequired &&
            !_scrollGuidanceShown;
        if (shouldShow)
        {
            _scrollGuidanceShown = true;
            ScrollGuidanceText.IsVisible = true;
        }
        else if (!Settings.ClickThrough || !scrollRequired)
        {
            ScrollGuidanceText.IsVisible = false;
        }
    }

    private double InformationWidthDip() => CurrentBaseWidgetWidthDip * (Settings.UiScalePercent / 100d);

    // 情報領域の高さはLayoutTransformControl（倍率適用後）の高さで測る。
    private double InformationHeightDip() => RootScale.Bounds.Height > 0 ? RootScale.Bounds.Height : Bounds.Height;

    private (double X, double Y) InformationInsetsDip() =>
        (
            Math.Max(0, (Bounds.Width - InformationWidthDip()) / 2d),
            Math.Max(0, (Bounds.Height - InformationHeightDip()) / 2d));
}
