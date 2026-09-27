using System.Runtime.Versioning;
using AiUsageMonitor.App.UI.Settings;
using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Mac;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace AiUsageMonitor.App.Mac;

/// <summary>
/// macOSの最小host。デモ用の利用データでウィジェットを表示し、メニューバーから層・透過・表示を切り替える。
/// P0-2（常駐表示の挙動）をAvalonia 12で手動再確認するためのもので、設定は保存しない。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class DemoWidgetHost : IDisposable
{
    private const double ScreenMarginDip = 12;
    private const string ClaudeSetupExampleText =
        "デモ表示ではClaude Codeとの接続や設定保存を行いません。通常起動で連携設定を確認してください。";

    private readonly Action _shutdown;
    private readonly UsageViewModel _viewModel = DemoUsageData.Create();
    private readonly IWidgetLayerController _layer;
    private AppSettings _settings = new AppSettings { ShowClaudeUsage = true }.Normalized();
    private bool _desktopBottom;
    private MainWindow? _main;
    private TrayIcon? _tray;
    private NativeMenuItem? _topmostItem;
    private NativeMenuItem? _bottomItem;
    private NativeMenuItem? _clickThroughItem;

    public DemoWidgetHost(Action shutdown, IWidgetLayerController? layer = null)
    {
        _shutdown = shutdown;
        _layer = layer ?? new MacWidgetLayerController();
    }

    internal MainWindow? Window => _main;

    internal NativeMenu? TrayMenu => _tray?.Menu;

    public void Start()
    {
        _main = new MainWindow { DataContext = _viewModel };
        _main.ApplySettings(_settings, reposition: false);
        _main.RepositionRequested += _ => PlaceTopRight();
        _main.Opened += (_, _) =>
        {
            _layer.Attach(_main.TryGetPlatformHandle()?.Handle ?? 0);
            ApplyLayer();
            PlaceTopRight();
        };
        _layer.HealthChanged += health =>
            Console.Error.WriteLine($"layer health: degraded={health.IsDegraded} operation={health.Operation}");
        _main.Show();
        _tray = CreateTray();
    }

    public void Dispose()
    {
        _main?.Close();
        _tray?.Dispose();
        _layer.Dispose();
    }

    internal void Apply(AppSettings settings)
    {
        _settings = settings.Normalized();
        if (_main is null) return;
        _main.ApplySettings(_settings, reposition: true);
        ApplyLayer();
        UpdateMenuChecks();
    }

    // 層とクリック透過はPlatform controllerだけが所有する。
    private void ApplyLayer()
    {
        WidgetLayerMode mode = _desktopBottom
            ? WidgetLayerMode.AlwaysOnBottom
            : _settings.AlwaysOnTop ? WidgetLayerMode.AlwaysOnTop : WidgetLayerMode.Normal;
        _layer.SetLayerMode(mode);
        _layer.SetClickThrough(_settings.ClickThrough);
    }

    private void PlaceTopRight()
    {
        if (_main is null) return;
        Screen? screen = _main.Screens.ScreenFromWindow(_main) ?? _main.Screens.Primary;
        if (screen is null) return;
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        PixelRect area = screen.WorkingArea;
        _main.SetWorkAreaHeight(area.Height / scaling);
        int width = (int)Math.Ceiling(_main.Bounds.Width * scaling);
        int margin = (int)Math.Round(ScreenMarginDip * scaling);
        _main.Position = new PixelPoint(area.Right - width - margin, area.Y + margin);
    }

    private TrayIcon CreateTray()
    {
        _topmostItem = new NativeMenuItem("常に手前に表示") { ToggleType = MenuItemToggleType.CheckBox };
        _topmostItem.Click += (_, _) =>
        {
            _desktopBottom = false;
            Apply(_settings with { AlwaysOnTop = !_settings.AlwaysOnTop });
        };
        _bottomItem = new NativeMenuItem("デスクトップ最背面") { ToggleType = MenuItemToggleType.CheckBox };
        _bottomItem.Click += (_, _) =>
        {
            _desktopBottom = !_desktopBottom;
            Apply(_desktopBottom ? _settings with { AlwaysOnTop = false } : _settings);
        };
        _clickThroughItem = new NativeMenuItem("クリックを透過") { ToggleType = MenuItemToggleType.CheckBox };
        _clickThroughItem.Click += (_, _) => Apply(_settings with { ClickThrough = !_settings.ClickThrough });

        var display = new NativeMenuItem("表示") { Menu = new NativeMenu() };
        display.Menu.Items.Add(Item("標準表示", () => Apply(_settings with { DisplayMode = WidgetDisplayMode.Standard })));
        display.Menu.Items.Add(Item("縮小表示", () => Apply(_settings with { DisplayMode = WidgetDisplayMode.Compact })));
        display.Menu.Items.Add(new NativeMenuItemSeparator());
        foreach (double scale in new[] { 100d, 150d, 200d })
            display.Menu.Items.Add(Item($"倍率 {scale:0}%", () => Apply(_settings with { UiScalePercent = scale })));
        display.Menu.Items.Add(new NativeMenuItemSeparator());
        display.Menu.Items.Add(Item("背景なし", () => Apply(_settings with { BackgroundEnabled = false })));
        display.Menu.Items.Add(Item("背景 単色", () => Apply(_settings with
        {
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.Solid,
        })));
        display.Menu.Items.Add(Item("背景 端を透明化", () => Apply(_settings with
        {
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
        })));

        var menu = new NativeMenu();
        menu.Items.Add(new NativeMenuItem("AI Usage Monitor（デモ表示）") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_topmostItem);
        menu.Items.Add(_bottomItem);
        menu.Items.Add(_clickThroughItem);
        menu.Items.Add(display);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("設定…", OpenSettings));
        menu.Items.Add(Item("ようこそ画面…", () => new WelcomeWindow().Show()));
        menu.Items.Add(Item("Claude Code連携…", OpenClaudeSetup));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("終了", _shutdown));
        UpdateMenuChecks();

        var tray = new TrayIcon
        {
            Icon = CreateTrayIcon(),
            ToolTipText = "AI Usage Monitor（デモ表示）",
            Menu = menu,
            IsVisible = true,
        };
        return tray;
    }

    private void UpdateMenuChecks()
    {
        if (_topmostItem is null || _bottomItem is null || _clickThroughItem is null) return;
        _topmostItem.IsChecked = _settings.AlwaysOnTop && !_desktopBottom;
        _bottomItem.IsChecked = _desktopBottom;
        _clickThroughItem.IsChecked = _settings.ClickThrough;
    }

    private void OpenSettings()
    {
        MonitorChoice[] monitors = (_main?.Screens.All ?? [])
            .Select((screen, index) => MonitorChoice.ForScreen(
                screen.DisplayName ?? $"Display {index + 1}",
                null,
                screen.IsPrimary))
            .ToArray();
        var window = new SettingsWindow(
            _settings,
            () => _settings,
            connectedMonitors: monitors,
            claudeSetupExample: ClaudeSetupExampleText);
        window.ClaudeSetupRequested += OpenClaudeSetup;
        window.Closed += (_, _) =>
        {
            if (window.Saved) Apply(window.Result);
        };
        window.Show();
    }

    private void OpenClaudeSetup()
    {
        UsageSnapshot status = UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with
        {
            Availability = UsageAvailability.Setup,
        };
        var window = new ClaudeSetupWindow(
            () => status,
            refresh: null,
            executablePath: null,
            workspace: new DemoWorkspace(),
            shellOpener: new MacShellOpener(),
            readmePath: Path.Combine(AppContext.BaseDirectory, "README.md"));
        window.Show();
    }

    private static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private static WindowIcon CreateTrayIcon()
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(144, 144));
        using (DrawingContext context = bitmap.CreateDrawingContext())
        {
            context.DrawEllipse(Brushes.White, null, new Point(9, 9), 7, 7);
            context.DrawRectangle(Brushes.White, null, new Rect(3, 13, 12, 2));
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    // デモではフォルダーを作成しない。製品ではPhase 3のIAppPathProvider実装が用途別パスを解決する。
    private sealed class DemoWorkspace : IClaudeWorkspaceProvisioner
    {
        public string WorkspacePath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "AiUsageMonitor",
            "ClaudeCliWorkspace");

        public string EnsureWorkspace() => WorkspacePath;
    }
}
