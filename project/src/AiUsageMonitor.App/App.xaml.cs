using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Windows;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Codex.Client;
using AiUsageMonitor.Codex.Runtime;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Claude.Windows.Cli;
using AiUsageMonitor.Claude.Windows.Console;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Windows.Startup;
using AiUsageMonitor.Windows.Process;

namespace AiUsageMonitor.App;

[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "WPF Application disposes owned resources in OnExit.")]
public partial class App : System.Windows.Application
{
    // Preserve pre-rename identifiers so existing settings and a running legacy
    // binary remain compatible with AI Usage Monitor.
    private const string LegacyMutexName = "Local\\CodexUsageMonitor-5C898151";
    private const string LegacySettingsDirectoryName = "CodexUsageMonitor";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly ClaudeManualRefreshState _manualRefreshState = new();
    private Mutex? _mutex;
    private FileSystemSettingsStore? _store;
    private AppSettings _settings = new();
    private MainWindow? _window;
    private UsageViewModel? _viewModel;
    private TrayController? _tray;
    private CodexAccountsCoordinator? _codexCoordinator;
    private Task? _codexPollTask;
    private Task? _claudePollTask;
    private ClaudeUsagePipeServer? _claudeServer;
    private ClaudeSetupWindow? _claudeSetupWindow;
    private readonly ClaudeUsageRuntime _claudeRuntime = new(configuration =>
        new ClaudeCliActiveSource(
            configuration.ExecutablePath,
            configuration.BridgePath,
            configuration.StartupTimeout));

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
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacySettingsDirectoryName,
            "settings.json");
        bool isFirstRun = !File.Exists(settingsPath);
        _store = new FileSystemSettingsStore(settingsPath);
        _settings = await _store.LoadAsync();
        _viewModel = new UsageViewModel();
        _viewModel.ShowCodex = CodexFeaturesEnabled();
        _viewModel.ShowClaude = _settings.ShowClaudeUsage;
        ConfigureCodexViewModels();
        _window = new MainWindow { DataContext = _viewModel };
        _window.ApplySettings(_settings, false);
        _window.UserPositionChanged += async () => { _settings = _window.CaptureCustomPosition(); await _store.SaveAsync(_settings); };
        _window.Show();
        _tray = new TrayController();
        _tray.ClaudeSetupRequested += ShowClaudeSetup;
        _tray.SettingsRequested += ShowSettings;
        _tray.RefreshRequested += () => _ = RefreshAsync(_lifetime.Token, manual: true);
        _tray.ToggleVisibilityRequested += () => { if (_window.IsVisible) _window.Hide(); else _window.Show(); };
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

    private async Task PollClaudeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            double jitter = 0.9 + (Random.Shared.NextDouble() * 0.2);
            await Task.Delay(TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds * jitter), cancellationToken);
            await RefreshAsync(cancellationToken, refreshCodex: false);
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
                    await current.WaitAsync(cancellationToken);
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
                await claudeRefresh;
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
        _settings = dialog.Result.Normalized();
        await _store.SaveAsync(_settings);
        // 表示切替をViewModelへ先に反映してからApplySettingsを呼ぶ。
        // 逆順だと保存前のセクション高で下端配置され、直後の高さ変化と競合する。
        _viewModel!.ShowCodex = CodexFeaturesEnabled();
        _viewModel!.ShowClaude = _settings.ShowClaudeUsage;
        _window.ApplySettings(_settings, true);
        await ConfigureCodexAsync();
        ConfigureClaudeListener();
        ApplyStartupSetting();
        await RefreshAsync(_lifetime.Token, manual: true);
        if (!wasClaudeEnabled && _settings.ShowClaudeUsage && !setupOpenedFromDialog) ShowClaudeSetup();
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
        _claudeRuntime.Disable();
        if (_codexPollTask is not null) try { await _codexPollTask; } catch (OperationCanceledException) { }
        if (_claudePollTask is not null) try { await _claudePollTask; } catch (OperationCanceledException) { }
        if (_codexCoordinator is not null) await _codexCoordinator.DisposeAsync();
        if (_claudeServer is not null) await _claudeServer.DisposeAsync();
        _tray?.Dispose(); _mutex?.Dispose(); _refreshGate.Dispose(); _lifetime.Dispose();
        base.OnExit(e);
    }
}

internal enum RefreshPhase
{
    Idle,
    Codex,
    Claude,
}
