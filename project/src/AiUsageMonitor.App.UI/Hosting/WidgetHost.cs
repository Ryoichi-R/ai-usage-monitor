using AiUsageMonitor.App.UI.Runtime;
using AiUsageMonitor.App.UI.Settings;
using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Codex.Runtime;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform;
using PlacementMode = AiUsageMonitor.Core.Settings.PlacementMode;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace AiUsageMonitor.App.UI.Hosting;

/// <summary>
/// OS共通のアプリ本体。WPF版<c>App.xaml.cs</c>の起動・定期更新・設定保存・初回案内・常駐アイコン操作を移したもの。
/// OS固有の処理は<see cref="WidgetHostServices"/>から受け取り、OS別thin hostは合成と起動だけを行う（D9）。
/// </summary>
public sealed class WidgetHost : IAsyncDisposable
{
    private readonly WidgetHostServices _services;
    private readonly Action _shutdown;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ClaudeManualRefreshState _manualRefreshState = new();
    private readonly ClaudeUsageRuntime _claudeRuntime;
    private readonly TrayDoubleClickDetector _trayDoubleClick = new(SystemDoubleClickTime);
    private FileSystemSettingsStore? _store;
    private AppSettings _settings = new AppSettings().Normalized();
    private UsageViewModel? _viewModel;
    private MainWindow? _window;
    private IWidgetLayerController? _layer;
    private BackgroundLayerCoordinator? _background;
    private CodexAccountsCoordinator? _codexCoordinator;
    private IClaudeUsageListener? _claudeListener;
    private Task? _codexPollTask;
    private Task? _claudePollTask;
    private TrayIcon? _tray;
    private NativeMenuItem? _standardModeItem;
    private NativeMenuItem? _compactModeItem;
    private SettingsWindow? _settingsWindow;
    private ClaudeSetupWindow? _claudeSetupWindow;
    private WelcomeWindow? _welcomeWindow;
    private bool _repositioning;
    private bool _layerDegraded;
    private bool _layerAttached;
    private bool _restoringInitialPlacement = true;
    private InformationGeometry? _initialPlacementGeometry;
    private string? _lastClaudeState;
    private int _disposed;

    public WidgetHost(WidgetHostServices services, Action shutdown)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _shutdown = shutdown ?? throw new ArgumentNullException(nameof(shutdown));
        _claudeRuntime = new(services.ClaudeSourceFactory);
    }

    public AppSettings Settings => _settings;

    public MainWindow? Window => _window;

    internal UsageViewModel? ViewModel => _viewModel;

    internal SettingsWindow? OpenSettingsWindow => _settingsWindow;

    internal ClaudeSetupWindow? OpenClaudeSetupWindow => _claudeSetupWindow;

    internal WelcomeWindow? OpenWelcomeWindow => _welcomeWindow;

    internal bool IsClaudeListening => _claudeListener is not null;

    /// <summary>起動が完了した（初回案内の表示を含む）。</summary>
    public event Action? Started;

    public async Task StartAsync()
    {
        string settingsPath = _services.AppPaths.SettingsFilePath;
        bool isFirstRun = !File.Exists(settingsPath);
        _store = new FileSystemSettingsStore(settingsPath);
        _settings = await _store.LoadAsync(_lifetime.Token);
        _viewModel = new UsageViewModel
        {
            ShowCodex = CodexFeaturesEnabled(),
            ShowClaude = _settings.ShowClaudeUsage,
        };
        ConfigureCodexViewModels();

        _window = new MainWindow { DataContext = _viewModel };
        _window.ApplySettings(_settings, reposition: false);
        _layer = _services.CreateLayerController();
        _layer.HealthChanged += OnLayerHealthChanged;
        _background = new BackgroundLayerCoordinator(_window, _services.CreateLayerController(), _services.Diagnostic);
        _window.RepositionRequested += Reposition;
        _window.UserMoveStarted += () => _restoringInitialPlacement = false;
        _window.UserMoveCompleted += () => _ = SaveUserPositionAsync();
        _window.Opened += (_, _) =>
        {
            if (!_layerAttached)
            {
                _layer.Attach(_window.TryGetPlatformHandle()?.Handle ?? 0);
                _layerAttached = true;
            }
            ApplyLayer();
            Reposition(true);
            RecordStartupPlacement("placement-startup-open");
        };
        _window.Screens.Changed += (_, _) => Dispatcher.UIThread.Post(() => Reposition(true));
        _window.Show();
        _tray = CreateTray();

        _codexCoordinator = new(_services.InheritedCodexHome, _services.UserProfile);
        _codexCoordinator.SnapshotChanged += DispatchCodexSnapshot;
        await ConfigureCodexAsync();
        _claudeRuntime.SnapshotChanged += DispatchClaudeSnapshot;
        ConfigureClaude();
        ApplyStartupSetting();
        await RefreshAsync(_lifetime.Token);
        // Opened runs before the initial layout and provider-dependent height settle.
        // Keep applying the saved intent until that first refresh has been laid out.
        Dispatcher.UIThread.Post(CompleteInitialPlacement, DispatcherPriority.Background);
        _codexPollTask = _codexCoordinator.RunPeriodicPollingAsync(_lifetime.Token);
        _claudePollTask = PollClaudeAsync(_lifetime.Token);
        if (isFirstRun) ShowWelcome();
        Started?.Invoke();
    }

    /// <summary>常駐中に2つ目の起動が要求されたときなど、ウィジェットを再表示する。</summary>
    public void ShowWidget()
    {
        if (_window is null || _window.IsVisible) return;
        _window.Show();
        ApplyLayer();
        Reposition(true);
    }

    internal void ToggleWidgetVisibility()
    {
        if (_window is null) return;
        if (_window.IsVisible) _window.Hide();
        else ShowWidget();
    }

    internal Task RequestRefreshAsync() => RefreshAsync(_lifetime.Token, manual: true);

    // ---- 配置・層 ----

    private void CompleteInitialPlacement()
    {
        if (_disposed != 0 || _window is null || !_restoringInitialPlacement) return;
        _window.UpdateLayout();
        Reposition(true);
        InformationGeometry geometry = _window.GetInformationGeometry();
        if (_initialPlacementGeometry != geometry)
        {
            // SizeToContent and scroll guidance may invalidate one another's layout.
            // Finish only after the geometry stays unchanged across layout passes.
            _initialPlacementGeometry = geometry;
            Dispatcher.UIThread.Post(CompleteInitialPlacement, DispatcherPriority.Background);
            return;
        }
        _restoringInitialPlacement = false;
        RecordStartupPlacement("placement-startup-settled");
    }

    private void RecordStartupPlacement(string phase)
    {
        if (_window is null) return;
        // Geometry only: never include provider payloads, usage values or account data.
        int height = (int)Math.Round(_window.GetInformationGeometry().HeightDip);
        _services.Diagnostic($"{phase}:height:{height}:y:{_window.Position.Y}", null);
    }

    private void Reposition(bool fullApply)
    {
        if (_window is null || _repositioning || _window.IsUserMoving) return;
        List<ScreenInfo> screens = CollectScreens();
        if (screens.Count == 0) return;
        InformationGeometry information = _window.GetInformationGeometry();
        if (information.WidthDip <= 0) return;
        _repositioning = true;
        try
        {
            ScreenInfo screen;
            PixelPoint position;
            if (fullApply || _restoringInitialPlacement || _settings.PlacementMode == PlacementMode.Preset)
            {
                screen = WidgetScreenPlacement.SelectScreen(screens, _settings.MonitorDeviceName, _settings.MonitorStableId)!.Value;
                _window.SetWorkAreaHeight(screen.DipWorkArea.Height);
                position = WidgetScreenPlacement.Calculate(screen, _window.GetInformationGeometry(), _settings);
            }
            else
            {
                screen = WidgetScreenPlacement.ScreenContaining(screens, WindowCenter())!.Value;
                _window.SetWorkAreaHeight(screen.DipWorkArea.Height);
                position = WidgetScreenPlacement.ClampCurrent(screen, _window.GetInformationGeometry(), _window.Position);
            }
            if (_window.Position != position) _window.Position = position;
        }
        finally
        {
            _repositioning = false;
        }
    }

    private PixelPoint WindowCenter()
    {
        MainWindow window = _window!;
        double scaling = window.DesktopScaling > 0 ? window.DesktopScaling : 1d;
        return new(
            window.Position.X + (int)Math.Round(window.Bounds.Width * scaling / 2d),
            window.Position.Y + (int)Math.Round(window.Bounds.Height * scaling / 2d));
    }

    private List<ScreenInfo> CollectScreens()
    {
        if (_window is null) return [];
        var result = new List<ScreenInfo>();
        int index = 0;
        foreach (Screen screen in _window.Screens.All)
        {
            index++;
            string? stableId = null;
            try { stableId = _services.ResolveScreenStableId?.Invoke(screen); }
            catch (Exception exception) { _services.Diagnostic("screen-stable-id-failure", exception); }
            result.Add(new(
                string.IsNullOrWhiteSpace(screen.DisplayName) ? $"Display {index}" : screen.DisplayName!,
                stableId,
                screen.IsPrimary,
                screen.WorkingArea,
                screen.Scaling));
        }
        return result;
    }

    /// <summary>現在位置を自由配置の設定として取り込む（設定画面の「現在位置を保存」と同じ）。</summary>
    internal AppSettings CaptureCurrentPosition()
    {
        if (_window is null) return _settings;
        List<ScreenInfo> screens = CollectScreens();
        if (WidgetScreenPlacement.ScreenContaining(screens, WindowCenter()) is not { } screen) return _settings;
        return WidgetScreenPlacement.Capture(_settings, screen, _window.GetInformationGeometry(), _window.Position);
    }

    private async Task SaveUserPositionAsync()
    {
        if (_store is null) return;
        AppSettings captured = CaptureCurrentPosition();
        if (captured == _settings) return;
        _settings = captured;
        _window?.ApplySettings(_settings, reposition: false);
        ApplyLayer();
        try
        {
            await _store.SaveAsync(_settings);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _services.Diagnostic("position-save-failure", exception);
        }
    }

    // nativeの層状態はIWidgetLayerControllerだけが所有する。
    private void ApplyLayer()
    {
        _background?.Apply(_settings);
        if (_layer is null || _window?.IsVisible != true) return;
        try
        {
            _layer.SetLayerMode(_settings.AlwaysOnTop ? WidgetLayerMode.AlwaysOnTop : WidgetLayerMode.Normal);
            _layer.SetClickThrough(_settings.ClickThrough);
        }
        catch (Exception exception)
        {
            _services.Diagnostic("widget-layer-apply-failure", exception);
        }
    }

    // 画面の表示文言だけでは原因の理由コードが分からないため、状態が変わったときだけ理由コードを診断へ残す。
    // 記録するのはavailabilityとreasonの列挙値だけで、使用率・reset時刻・account情報を含めない。
    private void ReportClaudeReason(UsageSnapshot snapshot)
    {
        string state = $"claude-status:{snapshot.Availability}:{snapshot.Reason ?? "-"}";
        if (string.Equals(state, _lastClaudeState, StringComparison.Ordinal)) return;
        _lastClaudeState = state;
        _services.Diagnostic(state, null);
    }

    private void OnLayerHealthChanged(WidgetLayerHealth health)
    {
        void Apply()
        {
            _layerDegraded = health.IsDegraded;
            if (health.IsDegraded) _services.Diagnostic("widget-layer-degraded:" + health.Operation, null);
            if (_tray is not null) _tray.ToolTipText = TrayToolTip();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private string TrayToolTip() => _layerDegraded ? "AI Usage Monitor（層の維持に失敗）" : "AI Usage Monitor";

    // ---- 常駐アイコン ----

    private TrayIcon? CreateTray()
    {
        if (_services.StatusIcon is null) return null;
        _standardModeItem = new NativeMenuItem("標準表示") { ToggleType = MenuItemToggleType.Radio };
        _standardModeItem.Click += (_, _) => _ = ApplyDisplayModeAsync(WidgetDisplayMode.Standard);
        _compactModeItem = new NativeMenuItem("縮小表示") { ToggleType = MenuItemToggleType.Radio };
        _compactModeItem.Click += (_, _) => _ = ApplyDisplayModeAsync(WidgetDisplayMode.Compact);
        var displayMode = new NativeMenuItem("表示モード") { Menu = new NativeMenu() };
        displayMode.Menu.Items.Add(_standardModeItem);
        displayMode.Menu.Items.Add(_compactModeItem);

        var menu = new NativeMenu();
        menu.Items.Add(MenuItem("Claude Code連携…", ShowClaudeSetup));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(MenuItem("設定…", ShowSettings));
        menu.Items.Add(MenuItem("今すぐ更新", () => _ = RequestRefreshAsync()));
        menu.Items.Add(MenuItem("表示／非表示", ToggleWidgetVisibility));
        menu.Items.Add(displayMode);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(MenuItem("終了", _shutdown));
        UpdateDisplayModeChecks();
        var tray = new TrayIcon
        {
            Icon = _services.StatusIcon,
            ToolTipText = TrayToolTip(),
            Menu = menu,
            IsVisible = true,
        };
        if (_services.OpenSettingsOnTrayDoubleClick) tray.Clicked += (_, _) => OnTrayClicked();
        return tray;
    }

    // WPF版と同じく、常駐アイコンのダブルクリックで設定画面を開く。
    internal void OnTrayClicked()
    {
        if (_trayDoubleClick.RegisterClick()) ShowSettings();
    }

    private static TimeSpan SystemDoubleClickTime() =>
        Application.Current?.PlatformSettings?.GetDoubleTapTime(Avalonia.Input.PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);

    private static NativeMenuItem MenuItem(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    private void UpdateDisplayModeChecks()
    {
        if (_standardModeItem is null || _compactModeItem is null) return;
        bool compact = _settings.DisplayMode == WidgetDisplayMode.Compact;
        _standardModeItem.IsChecked = !compact;
        _compactModeItem.IsChecked = compact;
    }

    internal async Task ApplyDisplayModeAsync(WidgetDisplayMode mode)
    {
        if (_window is null || _store is null || mode == _settings.DisplayMode)
        {
            UpdateDisplayModeChecks();
            return;
        }
        AppSettings candidate = (_settings with { DisplayMode = mode }).Normalized();
        try
        {
            await _store.SaveAsync(candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            UpdateDisplayModeChecks();
            ShowNotice($"表示モードを保存できませんでした。\n{exception.Message}");
            return;
        }
        _settings = candidate;
        _window.ApplySettings(_settings, reposition: true);
        ApplyLayer();
        UpdateDisplayModeChecks();
    }

    // ---- 画面 ----

    internal void ShowSettings()
    {
        if (_window is null || _store is null) return;
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }
        bool wasClaudeEnabled = _settings.ShowClaudeUsage;
        bool setupOpenedFromDialog = false;
        MonitorChoice[] monitors = CollectScreens()
            .Select(screen => MonitorChoice.ForScreen(screen.DeviceName, screen.StableId, screen.IsPrimary))
            .ToArray();
        var dialog = new SettingsWindow(
            _settings,
            CaptureCurrentPosition,
            CurrentClaude,
            CurrentClaudeSourceName,
            CurrentClaudeHealth,
            monitors,
            _services.ClaudeSetupExample);
        dialog.ClaudeSetupRequested += () =>
        {
            setupOpenedFromDialog = true;
            ShowClaudeSetup();
        };
        dialog.Closed += (_, _) =>
        {
            _settingsWindow = null;
            if (_disposed == 0 && dialog.Saved) _ = ApplySavedSettingsAsync(dialog.Result, wasClaudeEnabled, setupOpenedFromDialog);
        };
        _settingsWindow = dialog;
        dialog.Show();
        dialog.Activate();
    }

    internal async Task ApplySavedSettingsAsync(AppSettings result, bool wasClaudeEnabled, bool setupOpenedFromDialog)
    {
        if (_window is null || _store is null || _viewModel is null) return;
        AppSettings candidate = result.Normalized();
        try
        {
            await _store.SaveAsync(candidate);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowNotice($"設定を保存できませんでした。変更は適用されていません。\n{exception.Message}");
            return;
        }

        _settings = candidate;
        UpdateDisplayModeChecks();
        // 表示切替をViewModelへ先に反映してからApplySettingsを呼ぶ。
        // 逆順だと保存前のセクション高で配置され、直後の高さ変化と競合する。
        _viewModel.ShowCodex = CodexFeaturesEnabled();
        _viewModel.ShowClaude = _settings.ShowClaudeUsage;
        _window.ApplySettings(_settings, reposition: true);
        ApplyLayer();
        await ConfigureCodexAsync();
        ConfigureClaude();
        ApplyStartupSetting();
        await RefreshAsync(_lifetime.Token, manual: true);
        if (!wasClaudeEnabled && _settings.ShowClaudeUsage && !setupOpenedFromDialog) ShowClaudeSetup();
    }

    private void ShowWelcome()
    {
        if (_welcomeWindow is not null) return;
        var welcome = new WelcomeWindow();
        welcome.Closed += (_, _) =>
        {
            _welcomeWindow = null;
            if (_disposed == 0) _ = CompleteWelcomeAsync(welcome.UseClaude);
        };
        _welcomeWindow = welcome;
        welcome.Show();
        welcome.Activate();
    }

    internal async Task CompleteWelcomeAsync(bool useClaude)
    {
        if (_store is null || _viewModel is null) return;
        _settings = (_settings with { ShowClaudeUsage = useClaude }).Normalized();
        await _store.SaveAsync(_settings);
        _viewModel.ShowClaude = _settings.ShowClaudeUsage;
        ConfigureClaude();
        _window?.RepositionAfterContentChange();
        if (useClaude) ShowClaudeSetup();
    }

    internal void ShowClaudeSetup() => _ = ShowClaudeSetupAsync();

    private async Task ShowClaudeSetupAsync()
    {
        if (_window is null || _store is null || _viewModel is null) return;
        if (_claudeSetupWindow is not null)
        {
            _claudeSetupWindow.Activate();
            return;
        }
        if (!_settings.ShowClaudeUsage)
        {
            _settings = (_settings with { ShowClaudeUsage = true }).Normalized();
            await _store.SaveAsync(_settings);
            _viewModel.ShowClaude = true;
            ConfigureClaude();
        }

        var window = new ClaudeSetupWindow(
            CurrentClaude,
            () => _claudeRuntime.RefreshAsync(
                ClaudeUsageAcquisitionMode.OfficialCliOnly,
                manual: true,
                DateTimeOffset.UtcNow,
                _lifetime.Token),
            _settings.ClaudeExecutablePath,
            _services.ClaudeWorkspace,
            _services.ShellOpener,
            _services.ReadmePath);
        window.SetupSucceeded += () => _ = MarkClaudeSetupCompletedAsync();
        window.Closed += (_, _) => _claudeSetupWindow = null;
        _claudeSetupWindow = window;
        window.Show();
        window.Activate();
    }

    private async Task MarkClaudeSetupCompletedAsync()
    {
        if (_store is null) return;
        _settings = (_settings with { ClaudeSetupCompleted = true }).Normalized();
        await _store.SaveAsync(_settings);
    }

    private static void ShowNotice(string message)
    {
        var notice = new NoticeWindow(message);
        notice.Show();
        notice.Activate();
    }

    private void ApplyStartupSetting()
    {
        try
        {
            _services.Startup.Apply(_settings.StartWithWindows);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _services.Diagnostic("startup-apply-failure", exception);
        }
    }

    // ---- 取得 ----

    private async Task PollClaudeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            double jitter = 0.9 + (Random.Shared.NextDouble() * 0.2);
            await Task.Delay(TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds * jitter), cancellationToken);
            try
            {
                await RefreshAsync(cancellationToken, refreshCodex: false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // RefreshAsync内部で拾いきれない想定外の失敗でも、このループ自体は継続する。
                _services.Diagnostic("claude-poll-failure", exception);
            }
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken, bool manual = false, bool refreshCodex = true)
    {
        // Polling is authoritative (ADR 002); notifications are best-effort hints and may be coalesced.
        if (_viewModel is null) return;
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            if (manual)
            {
                ManualRefreshBusyDecision decision = _manualRefreshState.RequestWhileBusy();
                if (decision.JoinTask is { } current)
                {
                    try
                    {
                        await current.WaitAsync(cancellationToken);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // 合流先のowner呼び出しが既に失敗表示へ反映済みのため、ここでは再送出しない。
                    }
                }
            }
            return;
        }
        bool scheduleFollowUp;
        try
        {
            if (refreshCodex && CodexFeaturesEnabled() && _codexCoordinator is not null)
            {
                _manualRefreshState.Enter(RefreshPhase.Codex);
                try
                {
                    await _codexCoordinator.RefreshAsync(CodexRefreshOrigin.Manual, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _services.Diagnostic("codex-refresh-failure", exception);
                    _viewModel.Apply(UsageSnapshot.Loading(UsageProvider.Codex, DateTimeOffset.UtcNow) with
                    {
                        Availability = UsageAvailability.Error,
                        Reason = "CODEX_REFRESH_EXCEPTION",
                    });
                }
            }
            if (_settings.ShowClaudeUsage && _claudeRuntime.IsConfigured)
            {
                Task<UsageSnapshot> claudeRefresh = _claudeRuntime.RefreshAsync(
                    _settings.ClaudeUsageAcquisitionMode,
                    manual,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
                _manualRefreshState.Enter(RefreshPhase.Claude, claudeRefresh);
                try
                {
                    ReportClaudeReason(await claudeRefresh);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _services.Diagnostic("claude-refresh-failure", exception);
                    _viewModel.Apply(UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with
                    {
                        Availability = UsageAvailability.Error,
                        Reason = "CLAUDE_REFRESH_EXCEPTION",
                    });
                }
            }
        }
        finally
        {
            scheduleFollowUp = _manualRefreshState.CompleteOwner();
            _refreshGate.Release();
        }
        if (scheduleFollowUp)
        {
            _manualRefreshState.BeginDrain();
            try { await RefreshAsync(cancellationToken, manual: true); }
            finally { _manualRefreshState.EndDrain(); }
        }
    }

    private bool CodexFeaturesEnabled() =>
        _settings.ShowCodexUsage || _settings.ShowAdditionalUsage || _settings.ShowCredits;

    private async Task ConfigureCodexAsync()
    {
        if (_codexCoordinator is null) return;
        AppSettings runtimeSettings = CodexFeaturesEnabled()
            ? _settings
            : _settings with
            {
                CodexAccounts = _settings.CodexAccounts
                    .Select(account => account with { Enabled = false })
                    .ToArray(),
            };
        ConfigureCodexViewModels();
        await _codexCoordinator.ConfigureAsync(runtimeSettings, _services.CodexLifetimeGuardFactory, _services.CodexProcessLauncher, _lifetime.Token);
    }

    private void ConfigureCodexViewModels() =>
        _viewModel?.SynchronizeCodexAccounts(
            _settings.CodexAccounts,
            _settings.ShowCodexUsage,
            _settings.ShowAdditionalUsage,
            _settings.ShowCredits);

    private void DispatchCodexSnapshot(CodexAccountSnapshot snapshot)
    {
        void Apply()
        {
            if (_viewModel is null) return;
            _viewModel.Apply(snapshot);
            ConfigureCodexViewModels();
            _window?.RepositionAfterContentChange();
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void ConfigureClaude()
    {
        if (_settings.ShowClaudeUsage)
        {
            var configuration = new ClaudeActiveSourceConfiguration(
                _settings.ClaudeExecutablePath,
                _services.ClaudeBridgePath,
                TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds));
            ClaudeRuntimeSnapshot current = _claudeRuntime.Configure(
                configuration,
                DateTimeOffset.UtcNow,
                _settings.RefreshIntervalSeconds);
            ApplyClaudeSnapshot(current);
            if (_claudeListener is null && _services.CreateClaudeListener is { } createListener)
            {
                try
                {
                    IClaudeUsageListener listener = createListener();
                    listener.ObservationReceived += observation =>
                    {
                        if (_settings.ShowClaudeUsage) _claudeRuntime.ObservePassive(observation);
                    };
                    listener.Start();
                    _claudeListener = listener;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _services.Diagnostic("claude-listener-start-failure", exception);
                }
            }
        }
        else
        {
            _claudeRuntime.Disable();
            if (_claudeListener is { } listener)
            {
                _claudeListener = null;
                _ = listener.DisposeAsync().AsTask();
            }
        }
    }

    private void DispatchClaudeSnapshot(ClaudeRuntimeSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess()) ApplyClaudeSnapshot(snapshot);
        else Dispatcher.UIThread.Post(() => ApplyClaudeSnapshot(snapshot));
    }

    private void ApplyClaudeSnapshot(ClaudeRuntimeSnapshot candidate)
    {
        if (_viewModel is null || !_claudeRuntime.IsCurrent(candidate.Generation)) return;
        UsageSnapshot display = _claudeRuntime.ForDisplay(candidate.Snapshot, _settings.ClaudeSetupCompleted);
        _viewModel.ApplyClaude(candidate with { Snapshot = display });
        _window?.RepositionAfterContentChange();
    }

    private UsageSnapshot CurrentClaude()
    {
        ClaudeRuntimeSnapshot current = _claudeRuntime.Current(DateTimeOffset.UtcNow);
        return _claudeRuntime.ForDisplay(current.Snapshot, _settings.ClaudeSetupCompleted);
    }

    private string? CurrentClaudeSourceName() => _claudeRuntime.CurrentSource switch
    {
        ClaudeUsageSourceKind.StatusLinePassive => "Claude Code statusLine（常駐セッション）",
        ClaudeUsageSourceKind.StatusLineActive => "Claude Code statusLine（監視アプリ起動セッション）",
        ClaudeUsageSourceKind.CliScreen => "Claude Code CLI /usage 画面",
        _ => null,
    };

    private ClaudeRuntimeHealth CurrentClaudeHealth() => _claudeRuntime.CurrentHealth(DateTimeOffset.UtcNow);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        _claudeRuntime.Disable();
        // 通常はOperationCanceledExceptionだけで終わるが、想定外の例外が残っていても以降の破棄は必ず続ける。
        if (_codexPollTask is not null) try { await _codexPollTask; } catch (Exception) { }
        if (_claudePollTask is not null) try { await _claudePollTask; } catch (Exception) { }
        if (_codexCoordinator is not null) await _codexCoordinator.DisposeAsync();
        if (_claudeListener is not null) await _claudeListener.DisposeAsync();
        _background?.Dispose();
        _settingsWindow?.Close();
        _claudeSetupWindow?.Close();
        _welcomeWindow?.Close();
        _window?.Close();
        _tray?.Dispose();
        _layer?.Dispose();
        _store?.Dispose();
        _refreshGate.Dispose();
        _lifetime.Dispose();
    }
}
