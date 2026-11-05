using AiUsageMonitor.App.UI.Hosting;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.WorkStatus;
using AiUsageMonitor.Claude.Transport;
using Avalonia.Controls;
using PlacementMode = AiUsageMonitor.Core.Settings.PlacementMode;
using Avalonia.Media.Imaging;
using AiUsageMonitor.Platform;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class WidgetHostingTests
{
    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void PlacementRoundTripsAcrossCoordinateScales(double scale)
    {
        var screen = new ScreenInfo("display", "id", true, new PixelRect(-1800, 100, 1600, 900), scale);
        var geometry = new InformationGeometry(280, 210, 12, 8);
        var settings = new AppSettings { PlacementMode = PlacementMode.Custom, CustomLeftFraction = .3, CustomTopFraction = .7 };
        PixelPoint position = WidgetScreenPlacement.Calculate(screen, geometry, settings);
        AppSettings saved = WidgetScreenPlacement.Capture(settings, screen, geometry, position);
        Assert.InRange(Math.Abs(saved.CustomLeftFraction!.Value - .3), 0, .002);
        Assert.InRange(Math.Abs(saved.CustomTopFraction!.Value - .7), 0, .002);
        Assert.Equal("id", saved.MonitorStableId);
        Assert.Equal(position, WidgetScreenPlacement.Calculate(screen, geometry, saved));
        Assert.Equal(position, WidgetScreenPlacement.ClampCurrent(screen, geometry, position));
    }

    [Fact]
    public void MissingMonitorFallsBackAndStableIdentityOverridesDisplayName()
    {
        ScreenInfo primary = new("primary", "one", true, new PixelRect(0, 0, 1000, 800), 1);
        ScreenInfo other = new("other", "two", false, new PixelRect(1000, 0, 1000, 800), 2);
        ScreenInfo[] screens = [primary, other];
        Assert.Equal(other, WidgetScreenPlacement.SelectScreen(screens, "primary", "two"));
        Assert.Equal(primary, WidgetScreenPlacement.SelectScreen(screens, "other", "missing"));
        Assert.Equal(other, WidgetScreenPlacement.SelectScreen(screens, "other", null));
        Assert.Equal(other, WidgetScreenPlacement.ScreenContaining(screens, new PixelPoint(1900, 400)));
        Assert.Equal(other, WidgetScreenPlacement.ScreenContaining(screens, new PixelPoint(2500, 400)));
        Assert.Null(WidgetScreenPlacement.SelectScreen([], null, null));
        Assert.Null(WidgetScreenPlacement.ScreenContaining([], default));
    }

    [AvaloniaFact]
    public void SplitBackgroundFollowsVisibilityPositionAndSettings()
    {
        var settings = new AppSettings
        {
            BackgroundEnabled = true,
            AlwaysOnTop = true,
            HideBackgroundBehindWindows = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
            BackgroundEdgeFadePercent = 25
        };
        var main = MainWindowTestSupport.ShowLaidOut(MainWindowTestSupport.CreateLoadingViewModel(), settings);
        var layer = new FakeLayer();
        using var coordinator = new BackgroundLayerCoordinator(main, layer, (_, _) => { });
        coordinator.Background.HandleOverrideForTest = 42;
        try
        {
            coordinator.Apply(settings);
            MainWindowTestSupport.Settle(main);
            coordinator.Sync();
            Assert.True(coordinator.Background.IsVisible);
            Assert.Equal(WidgetLayerMode.AlwaysOnBottom, layer.Mode);
            Assert.True(layer.ClickThrough);
            InformationGeometry info = main.GetInformationGeometry();
            Assert.Equal(info.WidthDip * 1.5, coordinator.Background.Width, 2);
            main.Position = new PixelPoint(600, 400);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(600 - (int)Math.Round(info.WidthDip * .25), coordinator.Background.Position.X);
            main.Hide();
            Assert.False(coordinator.Background.IsVisible);
            main.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.True(coordinator.Background.IsVisible);
            coordinator.Apply(settings with { HideBackgroundBehindWindows = false });
            Assert.False(coordinator.Background.IsVisible);
            coordinator.Apply(settings);
            main.Close();
            Assert.True(layer.Disposed);
        }
        finally { main.Close(); }
    }

    [AvaloniaFact]
    public async Task HostPersistsSettingsAndDisposesWindowsWithoutStartingCli()
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-host-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new TestPaths(root);
            using (var store = new FileSystemSettingsStore(paths.SettingsFilePath))
                await store.SaveAsync(new AppSettings { ShowCodexUsage = false, ShowClaudeUsage = false });
            var startup = new FakeStartup();
            var layers = new List<FakeLayer>();
            var services = new WidgetHostServices
            {
                AppPaths = paths,
                Startup = startup,
                ShellOpener = new FakeShell(),
                CreateLayerController = () => { var layer = new FakeLayer(); layers.Add(layer); return layer; },
                ClaudeSourceFactory = _ => new UnavailableClaudeUsageSource(),
                ClaudeWorkspace = new AppPathClaudeWorkspace(paths),
                ClaudeBridgePath = Path.Combine(root, "bridge"),
                ReadmePath = Path.Combine(root, "README.md"),
                InheritedCodexHome = null,
                UserProfile = root,
            };
            await using var host = new WidgetHost(services, () => { });
            await host.StartAsync();
            Assert.True(host.Window!.IsVisible);
            Assert.False(host.Window.Topmost); // native layer is the sole owner
            Assert.False(host.IsClaudeListening);
            Assert.Equal(WidgetLayerMode.AlwaysOnTop, layers[0].Mode);
            host.ToggleWidgetVisibility();
            Assert.False(host.Window.IsVisible);
            host.ShowWidget();
            Assert.True(host.Window.IsVisible);
            host.ToggleWidgetVisibility();
            host.ShowWidget();
            host.Window.WindowState = WindowState.Minimized;
            host.ShowWidget();
            Assert.Equal(WindowState.Normal, host.Window.WindowState);
            Assert.True(host.Window.IsVisible);
            Assert.Equal(1, layers[0].AttachCalls);
            // WPF版と同じく、常駐アイコンは1回のクリックでは何もせず、ダブルクリックで設定を開く。
            host.OnTrayClicked();
            Assert.Null(host.OpenSettingsWindow);
            host.OnTrayClicked();
            Assert.NotNull(host.OpenSettingsWindow);
            Assert.False(host.OpenSettingsWindow!.ShowInTaskbar);
            Assert.Equal("AI Usage Monitor 設定", host.OpenSettingsWindow.Title);
            Assert.Equal("AI Usage Monitor", host.TrayToolTip());
            host.OpenSettingsWindow.Close();
            Assert.Null(host.OpenSettingsWindow);
            host.ShowSettings();
            Assert.NotNull(host.OpenSettingsWindow);
            host.OpenSettingsWindow!.Close();
            await host.ApplyDisplayModeAsync(WidgetDisplayMode.Compact);
            await host.ApplySavedSettingsAsync(host.Settings with { StartWithWindows = true, AlwaysOnTop = false, ClickThrough = false }, false, false);
            Assert.True(startup.Enabled);
            Assert.Equal(WidgetLayerMode.Normal, layers[0].Mode);
            Assert.False(layers[0].ClickThrough);
            using var stored = new FileSystemSettingsStore(paths.SettingsFilePath);
            AppSettings restored = await stored.LoadAsync();
            Assert.Equal(WidgetDisplayMode.Compact, restored.DisplayMode);
            Assert.True(restored.StartWithWindows);
            await host.RequestRefreshAsync();
            await host.DisposeAsync();
            Assert.All(layers, layer => Assert.True(layer.Disposed));
            Assert.False(host.Window.IsVisible);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaudeSetupAndPassiveLifecycleRemainIsolatedAndReportFailures(bool failListener)
    {
        string root = Path.Combine(Path.GetTempPath(), "aiusage-claude-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new TestPaths(root);
            using (var store = new FileSystemSettingsStore(paths.SettingsFilePath))
                await store.SaveAsync(new AppSettings { ShowCodexUsage = false, ShowClaudeUsage = true });
            var listener = new FakeListener();
            var diagnostics = new List<string>();
            using var bitmap = new RenderTargetBitmap(new PixelSize(16, 16));
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            stream.Position = 0;
            var services = new WidgetHostServices
            {
                AppPaths = paths,
                Startup = new FailingStartup(),
                ShellOpener = new FakeShell(),
                CreateLayerController = () => new FakeLayer(),
                ClaudeSourceFactory = _ => new UnavailableClaudeUsageSource(),
                CreateClaudeListener = () => failListener ? throw new IOException("fixture") : listener,
                ClaudeWorkspace = new AppPathClaudeWorkspace(paths),
                ClaudeBridgePath = Path.Combine(root, "bridge"),
                ReadmePath = Path.Combine(root, "README.md"),
                ClaudeSetupExample = "{}",
                StatusIcon = new WindowIcon(stream),
                InheritedCodexHome = null,
                UserProfile = root,
                Diagnostic = (code, _) => diagnostics.Add(code),
            };
            await using var host = new WidgetHost(services, () => { });
            await host.StartAsync();
            Assert.Equal(!failListener, host.IsClaudeListening);
            Assert.Contains("startup-apply-failure", diagnostics);
            if (failListener) Assert.Contains("claude-listener-start-failure", diagnostics);
            else listener.Publish(UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow));
            host.ShowSettings();
            host.ShowSettings();
            Assert.NotNull(host.OpenSettingsWindow);
            host.OpenSettingsWindow!.Close();
            await host.CompleteWelcomeAsync(true);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(host.OpenClaudeSetupWindow);
            host.ShowClaudeSetup();
            await host.RequestRefreshAsync();
            await host.RequestRefreshAsync();
            // 理由コードは状態が変わったときだけ、列挙値として一度記録する。
            Assert.Single(diagnostics, code => code.StartsWith("claude-status:", StringComparison.Ordinal));
            await host.ApplyDisplayModeAsync(WidgetDisplayMode.Compact);
            await host.ApplyDisplayModeAsync(WidgetDisplayMode.Standard);
            await host.ApplySavedSettingsAsync(host.Settings with { ShowClaudeUsage = false }, true, true);
            Assert.False(host.IsClaudeListening);
            if (!failListener) Assert.True(listener.Disposed);
            await host.CompleteWelcomeAsync(false);
        }
        finally { Directory.Delete(root, true); }
    }

    [AvaloniaFact]
    public async Task WorkStatusTrialIsDiscoverableAndClosesWithoutUsageOrStartupChanges()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? Path.GetTempPath();
        string root = Path.Combine(parent, "work-status-trial", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new TestPaths(root);
        var startup = new FakeStartup();
        bool shutdown = false;
        var services = new WidgetHostServices
        {
            AppPaths = paths,
            WorkStatusOnly = true,
            Startup = startup,
            ShellOpener = new FakeShell(),
            CreateLayerController = () => new FakeLayer(),
            ClaudeSourceFactory = _ => throw new InvalidOperationException("Usage must stay disabled."),
            CreateClaudeListener = () => throw new InvalidOperationException("Usage must stay disabled."),
            ClaudeWorkspace = new AppPathClaudeWorkspace(paths),
            ClaudeBridgePath = Path.Combine(root, "bridge"),
            ReadmePath = Path.Combine(root, "README.md"),
            UserProfile = root,
        };
        await using var host = new WidgetHost(services, () => shutdown = true);
        await host.StartAsync();
        Assert.True(host.Window!.ShowInTaskbar);
        Assert.Equal("AI Usage Monitor（隔離検証）", host.TrayToolTip());
        host.Window.WindowState = WindowState.Minimized;
        host.ShowWidget();
        Assert.Equal(WindowState.Normal, host.Window.WindowState);
        Assert.True(host.Window.IsVisible);
        host.ShowSettings();
        Assert.True(host.OpenSettingsWindow!.ShowInTaskbar);
        Assert.Equal("AI Usage Monitor 設定 - 隔離検証", host.OpenSettingsWindow.Title);
        host.OpenSettingsWindow.Close();
        Assert.Equal(WindowDecorations.Full, host.Window.WindowDecorations);
        Assert.Equal("AI Usage Monitor - 作業状態の隔離検証", host.Window.Title);
        Assert.False(host.Settings.ShowCodexUsage);
        Assert.False(host.Settings.ShowClaudeUsage);
        Assert.True(host.Settings.ShowCodexWorkStatus);
        Assert.True(host.Settings.ShowClaudeWorkStatus);
        Assert.False(host.IsClaudeListening);
        using (var savedSettings = new FileSystemSettingsStore(paths.SettingsFilePath))
            await savedSettings.SaveAsync(host.Settings);
        byte[] beforeSetup = File.ReadAllBytes(paths.SettingsFilePath);
        host.ShowClaudeSetup();
        await WaitForAsync(() => host.OpenClaudeSetupWindow is not null);
        Assert.False(host.Settings.ShowClaudeUsage);
        Assert.False(host.ViewModel!.ShowClaude);
        Assert.Equal("Claude Code連携 - 隔離検証", host.OpenClaudeSetupWindow!.Title);
        Assert.True(host.OpenClaudeSetupWindow.ShowInTaskbar);
        Assert.False(host.OpenClaudeSetupWindow.TestConnectionButton.IsEnabled);
        await host.OpenClaudeSetupWindow.TestConnectionAsync();
        Assert.False(host.IsClaudeListening);
        Assert.Equal(beforeSetup, File.ReadAllBytes(paths.SettingsFilePath));
        host.OpenClaudeSetupWindow.Close();
        await host.RequestRefreshAsync();
        Assert.False(host.ViewModel.ShowClaude);
        await host.ApplySavedSettingsAsync(host.Settings with { StartWithWindows = true }, false, false);
        Assert.False(startup.Enabled);
        host.Window.Close();
        Assert.True(shutdown);
    }

    [AvaloniaFact]
    public async Task TrayAcknowledgesWithoutOpeningAndKeepsAnOlderMenuSnapshot()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory;
        string root = Path.Combine(parent, "work-tray", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IAppPathProvider paths = new TestPaths(root);
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "interrupted", WorkProvider.CodexDesktop, "first", "t", WorkEventKind.Interrupted, now, true));
        model.Sessions.Add(new(WorkProvider.CodexDesktop, "old", "t", WorkSessionState.Unknown, now, 1));
        model.Apply(new(1, "claude", WorkProvider.ClaudeCode, "claude", "t", WorkEventKind.StopCandidate, now, true));
        var codes = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var store = new WorkStatusStore(paths.WorkStatusDirectory);
        store.Save(model);
        using var bitmap = new RenderTargetBitmap(new PixelSize(16, 16));
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        var services = new WidgetHostServices
        {
            AppPaths = paths,
            WorkStatusOnly = true,
            Startup = new FakeStartup(),
            ShellOpener = new FakeShell(),
            CreateLayerController = () => new FakeLayer(),
            ClaudeSourceFactory = _ => throw new InvalidOperationException("No usage probes."),
            CreateClaudeListener = () => throw new InvalidOperationException("No usage probes."),
            ClaudeWorkspace = new AppPathClaudeWorkspace(paths),
            ClaudeBridgePath = Path.Combine(root, "bridge"),
            ReadmePath = Path.Combine(root, "README.md"),
            UserProfile = root,
            StatusIcon = new WindowIcon(stream),
            Diagnostic = (code, error) => codes.Enqueue(code + ":" + error?.GetType().Name + ":" + error?.HResult),
        };
        await using var host = new WidgetHost(services, () => { });
        await host.StartAsync();
        NativeMenu firstMenu = Assert.IsType<NativeMenu>(host.TrayMenu);
        NativeMenuItem item = Assert.Single(firstMenu.Items.OfType<NativeMenuItem>(), i => i.Header == "Codex Desktopの通知を確認済みにする");
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<string>>(item.CommandParameter).Count);
        Assert.True(item.Command!.CanExecute(item.CommandParameter));
        WorkStatusCollector.WriteEvent(paths.WorkStatusDirectory,
            new(1, "new", WorkProvider.CodexDesktop, "new", "t", WorkEventKind.Interrupted, now.AddMilliseconds(1), true));
        await WaitForAsync(() => host.ViewModel!.WorkStatus.Rows[0].NoticeIds.Count == 3);
        Assert.NotSame(firstMenu, host.TrayMenu);
        // The Win32 presenter invokes the command without NativeMenu.Opening.
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<string>>(item.CommandParameter).Count);
        bool requested = false;
        host.ViewModel!.WorkStatus.Rows[0].AcknowledgeRequested += _ => requested = true;
        item.Command.Execute(item.CommandParameter);
        Assert.True(requested, "The retained menu command must dispatch acknowledgement.");
        await WaitForAsync(() => store.Load().AcknowledgedIds.Count == 2, () => string.Join(",", codes));
        WorkStatusModel saved = store.Load();
        WorkStatusSummary codex = saved.Summarize(WorkProvider.CodexDesktop, DateTimeOffset.UtcNow, true);
        Assert.Equal(0, codex.Unknown);
        Assert.Equal("new", Assert.Single(codex.Notices).SessionId);
        Assert.Equal(1, saved.Summarize(WorkProvider.ClaudeCode, DateTimeOffset.UtcNow, true).Candidates);
    }

    private static async Task WaitForAsync(Func<bool> condition, Func<string>? details = null)
    {
        for (int attempt = 0; attempt < 250; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(20);
        }
        Assert.True(condition(), "The asynchronous work-state operation did not finish. " + details?.Invoke());
    }

    private sealed class FakeListener : IClaudeUsageListener
    {
        public event Action<UsageSnapshot>? ObservationReceived;
        public bool Disposed { get; private set; }
        public void Start() { }
        public void Publish(UsageSnapshot snapshot) => ObservationReceived?.Invoke(snapshot);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class FailingStartup : IStartupService
    {
        public void Apply(bool startWithSystem) => throw new IOException("fixture");
    }

    private sealed class TestPaths(string root) : IAppPathProvider
    {
        public string SettingsFilePath => Path.Combine(root, "settings.json");
        public string ClaudeWorkspaceDirectory => Path.Combine(root, "workspace");
        public string TemporaryDirectory => root;
        public string UserHomeDirectory => root;
    }
    private sealed class FakeStartup : IStartupService
    {
        public bool Enabled { get; private set; }
        public void Apply(bool startWithSystem) => Enabled = startWithSystem;
    }
    private sealed class FakeShell : IShellOpener
    {
        public void OpenFolder(string path) { }
    }
    private sealed class FakeLayer : IWidgetLayerController
    {
        public WidgetLayerHealth Health => new(false, null, 0);
        public event Action<WidgetLayerHealth>? HealthChanged { add { } remove { } }
        public WidgetLayerMode Mode { get; private set; }
        public bool ClickThrough { get; private set; }
        public bool Disposed { get; private set; }
        public int AttachCalls { get; private set; }
        public void Attach(nint nativeWindowHandle)
        {
            if (++AttachCalls > 1) throw new InvalidOperationException("Already attached to a window handle.");
        }
        public void SetLayerMode(WidgetLayerMode mode) => Mode = mode;
        public void SetClickThrough(bool enabled) => ClickThrough = enabled;
        public bool TryRecoverLayer() => true;
        public void Dispose() => Disposed = true;
    }
}
