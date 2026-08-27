using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Windows;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Codex.Client;
using AiUsageMonitor.Codex.Runtime;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Claude.Windows.Cli;
using AiUsageMonitor.Claude.Windows.Console;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Windows;
using AiUsageMonitor.Platform.Windows.Startup;
using AiUsageMonitor.Platform.Windows.Process;
using ClaudeWorkspaceProvisioner = AiUsageMonitor.Claude.Windows.Process.ClaudeWorkspaceProvisioner;
using ClaudeExecutableLocator = AiUsageMonitor.Claude.Windows.Process.ClaudeExecutableLocator;

namespace AiUsageMonitor.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Application disposes owned resources in OnExit.")]
public partial class App : System.Windows.Application
{
    // Preserve pre-rename identifiers so existing settings and a running legacy
    // binary remain compatible with AI Usage Monitor.
    // 設定ディレクトリ名の互換は WindowsAppPathProvider が保持する。
    private const string LegacyMutexName = "Local\\CodexUsageMonitor-5C898151";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ClaudeManualRefreshState _manualRefreshState = new();
    private Mutex? _mutex;
    private FileSystemSettingsStore? _store;
    private AppSettings _settings = new();
    private MainWindow? _window;
    private BackgroundLayerCoordinator? _backgroundCoordinator;
    private UsageViewModel? _viewModel;
    private TrayController? _tray;
    private CodexAccountsCoordinator? _codexCoordinator;
    private Task? _codexPollTask;
    private Task? _claudePollTask;
    private ClaudeUsagePipeServer? _claudeServer;
    private ClaudeSetupWindow? _claudeSetupWindow;
    // Phase 1時点でこのhostはWindows専用のため具象型で保持する。抽象境界は
    // ClaudeWorkspaceProvisionerがIAppPathProviderを受け取る点で成立している。
    private static readonly WindowsAppPathProvider AppPaths = new();
    private readonly ClaudeUsageRuntime _claudeRuntime = new(configuration =>
        new ClaudeCliActiveSource(
            configuration.ExecutablePath,
            configuration.BridgePath,
            configuration.StartupTimeout,
            new ClaudeWorkspaceProvisioner(AppPaths),
            new WindowsClaudeScreenSessionFactory(),
            new ClaudeExecutableLocator()));

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (ClaudeConsoleHelper.TryHandle(e.Args, System.Console.Out))
        {
            Shutdown(0);
            return;
        }
        _mutex = new Mutex(true, LegacyMutexName, out bool first);
        if (!first) { Shutdown(); return; }
        // 起動処理は多数の外部リソース（設定file、レジストリ、Codex/Claude起動）に触れる。
        // ここで想定外の例外が漏れるとasync void経由で無表示のままクラッシュするため、
        // 致命的な失敗だけは理由を提示してから終了する。
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        try
        {
            string settingsPath = AppPaths.SettingsFilePath;
            bool isFirstRun = !File.Exists(settingsPath);
            _store = new FileSystemSettingsStore(settingsPath);
            _settings = await _store.LoadAsync();
            _viewModel = new UsageViewModel();
            _viewModel.ShowCodex = CodexFeaturesEnabled();
            _viewModel.ShowClaude = _settings.ShowClaudeUsage;
            ConfigureCodexViewModels();
            _window = new MainWindow { DataContext = _viewModel };
            _window.ApplySettings(_settings, false);
            _backgroundCoordinator = new BackgroundLayerCoordinator(_window, Dispatcher);
            _backgroundCoordinator.Apply(_settings);
            _window.UserPositionChanged += async () => { _settings = _window.CaptureCustomPosition(); await _store.SaveAsync(_settings); };
            _window.Show();
            _tray = new TrayController();
            _window.TopmostHealthChanged += degraded => _tray?.SetTopmostDegraded(degraded);
            _tray.SetTopmostDegraded(_window.IsTopmostDegraded);
            _tray.SetDisplayMode(_settings.DisplayMode);
            _tray.ClaudeSetupRequested += ShowClaudeSetup;
            _tray.SettingsRequested += ShowSettings;
            _tray.RefreshRequested += () => _ = RefreshAsync(_lifetime.Token, manual: true);
            _tray.ToggleVisibilityRequested += () => _backgroundCoordinator?.SetWidgetVisible(!_window.IsVisible);
            _tray.DisplayModeRequested += mode => _ = ApplyDisplayModeAsync(mode);
            _tray.ExitRequested += Shutdown;
            _codexCoordinator = new(
                Environment.GetEnvironmentVariable("CODEX_HOME"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            _codexCoordinator.SnapshotChanged += DispatchCodexSnapshot;
            await ConfigureCodexAsync();
            _claudeRuntime.SnapshotChanged += DispatchClaudeSnapshot;
            ConfigureClaudeListener();
            await RefreshAsync(_lifetime.Token);
            _codexPollTask = _codexCoordinator.RunPeriodicPollingAsync(_lifetime.Token);
            _claudePollTask = PollClaudeAsync(_lifetime.Token);
            if (isFirstRun) await ShowFirstRunAsync();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"AI Usage Monitorを起動できませんでした。設定ファイルが壊れているか、必要なリソースにアクセスできない可能性があります。\n\n{exception.GetType().Name}: {exception.Message}",
                "AI Usage Monitor",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        // UI操作由来のasync void経路（設定保存、位置保存など）で想定外の例外が起きても、
        // 既定動作（無表示のままプロセス終了）にはしない。操作は失敗として扱い続行する。
        System.Windows.MessageBox.Show(
            $"操作に失敗しました。直前の変更は保存されていない可能性があります。\n\n{e.Exception.GetType().Name}: {e.Exception.Message}",
            "AI Usage Monitor",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Warning);
        e.Handled = true;
    }

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
                // ここで止まるとClaudeの定期更新が無表示のまま恒久停止してしまう。
            }
        }
    }

    private async Task RefreshAsync(
        CancellationToken cancellationToken,
        bool manual = false,
        bool refreshCodex = true)
    {
        // Polling is authoritative (ADR 002); notifications are best-effort hints and may be coalesced.
        if (_viewModel is null) return;
        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            if (manual)
            {
                ManualRefreshBusyDecision decision =
                    _manualRefreshState.RequestWhileBusy();
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
        bool scheduleFollowUp = false;
        try
        {
            if (refreshCodex && CodexFeaturesEnabled() && _codexCoordinator is not null)
            {
                _manualRefreshState.Enter(RefreshPhase.Codex);
                try
                {
                    await _codexCoordinator.RefreshAsync(
                        CodexRefreshOrigin.Manual,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
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
                    await claudeRefresh;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
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
        await _codexCoordinator.ConfigureAsync(
            runtimeSettings,
            ProcessJobObject.Attach,
            _lifetime.Token);
    }

    private void ConfigureCodexViewModels()
    {
        if (_viewModel is null) return;
        _viewModel.SynchronizeCodexAccounts(
            _settings.CodexAccounts,
            _settings.ShowCodexUsage,
            _settings.ShowAdditionalUsage,
            _settings.ShowCredits);
    }

    private void DispatchCodexSnapshot(CodexAccountSnapshot snapshot)
    {
        if (_viewModel is null) return;
        void Apply()
        {
            _viewModel.Apply(snapshot);
            ConfigureCodexViewModels();
            _window?.RepositionAfterContentChange();
        }
        if (Dispatcher.CheckAccess()) Apply();
        else Dispatcher.Invoke(Apply);
    }

    private async void ShowSettings()
    {
        if (_window is null || _store is null) return;
        bool wasClaudeEnabled = _settings.ShowClaudeUsage;
        var dialog = new SettingsWindow(
            _settings,
            _window.CaptureCustomPosition,
            CurrentClaude,
            CurrentClaudeSourceName,
            CurrentClaudeHealth)
        {
            Owner = _window,
        };
        bool setupOpenedFromDialog = false;
        dialog.ClaudeSetupRequested += () =>
        {
            setupOpenedFromDialog = true;
            ShowClaudeSetup();
        };
        if (dialog.ShowDialog() != true) return;
        AppSettings candidate = dialog.Result.Normalized();
        try
        {
            await _store.SaveAsync(candidate);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"設定を保存できませんでした。変更は適用されていません。\n{exception.Message}",
                "AI Usage Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _settings = candidate;
        _tray?.SetDisplayMode(_settings.DisplayMode);
        // 表示切替をViewModelへ先に反映してからApplySettingsを呼ぶ。
        // 逆順だと保存前のセクション高で下端配置され、直後の高さ変化と競合する。
        _viewModel!.ShowCodex = CodexFeaturesEnabled();
        _viewModel!.ShowClaude = _settings.ShowClaudeUsage;
        _window.ApplySettings(_settings, true);
        _backgroundCoordinator?.Apply(_settings);
        await ConfigureCodexAsync();
        ConfigureClaudeListener();
        ApplyStartupSetting();
        await RefreshAsync(_lifetime.Token, manual: true);
        if (!wasClaudeEnabled && _settings.ShowClaudeUsage && !setupOpenedFromDialog) ShowClaudeSetup();
    }

    private async Task ApplyDisplayModeAsync(WidgetDisplayMode mode)
    {
        if (_window is null || _store is null || mode == _settings.DisplayMode) return;
        AppSettings previous = _settings;
        AppSettings candidate = (_settings with { DisplayMode = mode }).Normalized();
        try
        {
            await _store.SaveAsync(candidate);
        }
        catch (Exception exception)
        {
            _tray?.SetDisplayMode(previous.DisplayMode);
            System.Windows.MessageBox.Show(
                $"表示モードを保存できませんでした。\n{exception.Message}",
                "AI Usage Monitor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _settings = candidate;
        _window.ApplySettings(_settings, reposition: true);
        _backgroundCoordinator?.Apply(_settings);
        _tray?.SetDisplayMode(_settings.DisplayMode);
    }

    private async Task ShowFirstRunAsync()
    {
        if (_window is null || _store is null) return;
        var welcome = new WelcomeWindow { Owner = _window };
        welcome.ShowDialog();
        _settings = (_settings with { ShowClaudeUsage = welcome.UseClaude }).Normalized();
        await _store.SaveAsync(_settings);
        _viewModel!.ShowClaude = _settings.ShowClaudeUsage;
        ConfigureClaudeListener();
        if (welcome.UseClaude) ShowClaudeSetup();
    }

    private async void ShowClaudeSetup()
    {
        if (_window is null || _store is null) return;
        if (_claudeSetupWindow is not null)
        {
            _claudeSetupWindow.Activate();
            return;
        }
        if (!_settings.ShowClaudeUsage)
        {
            _settings = (_settings with { ShowClaudeUsage = true }).Normalized();
            await _store.SaveAsync(_settings);
            _viewModel!.ShowClaude = true;
            ConfigureClaudeListener();
        }

        _claudeSetupWindow = new ClaudeSetupWindow(
            CurrentClaude,
            () => RunClaudeConnectionTestAsync(
                _claudeRuntime,
                DateTimeOffset.UtcNow,
                _lifetime.Token),
            _settings.ClaudeExecutablePath)
        {
            Owner = System.Windows.Application.Current.Windows
                .OfType<Window>()
                .FirstOrDefault(candidate => candidate.IsActive) ?? _window,
        };
        _claudeSetupWindow.SetupSucceeded += async () =>
        {
            _settings = (_settings with { ClaudeSetupCompleted = true }).Normalized();
            await _store.SaveAsync(_settings);
        };
        try
        {
            _claudeSetupWindow.ShowDialog();
        }
        finally
        {
            _claudeSetupWindow = null;
        }
    }

    private void ApplyStartupSetting()
    {
        StartupRegistryService.Apply(_settings.StartWithWindows);
    }

    private void ConfigureClaudeListener()
    {
        if (_settings.ShowClaudeUsage)
        {
            string bridgePath = Path.Combine(AppContext.BaseDirectory, "claude-statusline-bridge.ps1");
            var configuration = new ClaudeActiveSourceConfiguration(
                _settings.ClaudeExecutablePath,
                bridgePath,
                TimeSpan.FromSeconds(_settings.StartupTimeoutSeconds));
            ClaudeRuntimeSnapshot current = _claudeRuntime.Configure(
                configuration,
                DateTimeOffset.UtcNow,
                _settings.RefreshIntervalSeconds);
            ApplyClaudeSnapshot(current);
            if (_claudeServer is null)
            {
                _claudeServer = new ClaudeUsagePipeServer();
                _claudeServer.ObservationReceived += observation =>
                {
                    if (_settings.ShowClaudeUsage)
                        _claudeRuntime.ObservePassive(observation);
                };
                _claudeServer.Start();
            }
        }
        else
        {
            _claudeRuntime.Disable();
            if (_claudeServer is not null)
            {
                ClaudeUsagePipeServer server = _claudeServer;
                _claudeServer = null;
                _ = DisposeClaudeServerAsync(server);
            }
        }
    }

    internal static Task<UsageSnapshot> RunClaudeConnectionTestAsync(
        ClaudeUsageRuntime runtime,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.OfficialCliOnly,
            manual: true,
            now,
            cancellationToken);
    }

    private void DispatchClaudeSnapshot(ClaudeRuntimeSnapshot snapshot)
    {
        if (Dispatcher.CheckAccess())
            ApplyClaudeSnapshot(snapshot);
        else
            Dispatcher.Invoke(() => ApplyClaudeSnapshot(snapshot));
    }

    private void ApplyClaudeSnapshot(ClaudeRuntimeSnapshot candidate)
    {
        if (_viewModel is null || !_claudeRuntime.IsCurrent(candidate.Generation)) return;
        UsageSnapshot display = _claudeRuntime.ForDisplay(
            candidate.Snapshot,
            _settings.ClaudeSetupCompleted);
        _viewModel.ApplyClaude(candidate with { Snapshot = display }, _settings.ClaudeSetupCompleted);
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

    private ClaudeRuntimeHealth CurrentClaudeHealth() =>
        _claudeRuntime.CurrentHealth(DateTimeOffset.UtcNow);

    private static async Task DisposeClaudeServerAsync(ClaudeUsagePipeServer server) => await server.DisposeAsync();

    protected override async void OnExit(ExitEventArgs e)
    {
        _lifetime.Cancel();
        _backgroundCoordinator?.Dispose();
        _claudeRuntime.Disable();
        // 通常はOperationCanceledExceptionだけで終わるが、pollループ内で拾いきれない
        // 想定外の例外が残っていた場合でも、以降の破棄処理とアプリ終了は必ず継続する。
        if (_codexPollTask is not null) try { await _codexPollTask; } catch (Exception) { }
        if (_claudePollTask is not null) try { await _claudePollTask; } catch (Exception) { }
        if (_codexCoordinator is not null) await _codexCoordinator.DisposeAsync();
        if (_claudeServer is not null) await _claudeServer.DisposeAsync();
        _tray?.Dispose(); _mutex?.Dispose(); _store?.Dispose(); _refreshGate.Dispose(); _lifetime.Dispose();
        base.OnExit(e);
    }
}

internal enum RefreshPhase
{
    Idle,
    Codex,
    Claude,
}
