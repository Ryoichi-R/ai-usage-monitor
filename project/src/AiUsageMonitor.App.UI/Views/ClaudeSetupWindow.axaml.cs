using AiUsageMonitor.App.UI.Onboarding;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace AiUsageMonitor.App.UI.Views;

/// <summary>
/// Claude Code自動取得の初期設定。WPF版<c>ClaudeSetupWindow</c>の移植で、専用フォルダーの用意・
/// フォルダーを開く操作・起動コマンドの形式はOS別の実装を注入する。
/// </summary>
public partial class ClaudeSetupWindow : Window
{
    private const string LlmSetupReadmeHeading = "LLMによるセットアップ支援（非推奨）";

    private readonly Func<UsageSnapshot> _statusProvider;
    private readonly Func<Task<UsageSnapshot>>? _refresh;
    private readonly IShellOpener? _shellOpener;
    private readonly string _readmePath;
    private readonly string _settingsFolder;
    private readonly DispatcherTimer _statusTimer;

    // XAMLローダー（デザイナー）用。
    public ClaudeSetupWindow()
        : this(() => UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow), null, null, null, null, "README.md")
    {
    }

    public ClaudeSetupWindow(
        Func<UsageSnapshot> statusProvider,
        Func<Task<UsageSnapshot>>? refresh,
        string? executablePath,
        IClaudeWorkspaceProvisioner? workspace,
        IShellOpener? shellOpener,
        string readmePath)
    {
        ArgumentNullException.ThrowIfNull(statusProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(readmePath);
        InitializeComponent();
        _statusProvider = statusProvider;
        _refresh = refresh;
        _shellOpener = shellOpener;
        _readmePath = readmePath;
        _settingsFolder = workspace?.EnsureWorkspace() ?? string.Empty;
        SettingsFolderBox.Text = _settingsFolder;
        ClaudeCommandBox.Text = _settingsFolder.Length == 0
            ? string.Empty
            : ClaudeTrustCommand.CreateForCurrentPlatform(_settingsFolder, executablePath);
        string shell = OperatingSystem.IsWindows() ? "PowerShell" : "ターミナル";
        DesktopLimitationText.Text =
            $"下のコマンドを{shell}へ貼り付け、公式CLIの信頼確認で専用フォルダーを確認してから承認してください。監視アプリはこの選択を代行しません。承認後はEscで終了できます。";
        _statusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStatus());
        Opened += (_, _) => RefreshStatus();
        Closed += (_, _) => _statusTimer.Stop();
        _statusTimer.Start();
        RefreshStatus();
    }

    public event Action? SetupSucceeded;

    internal void RefreshStatus()
    {
        UsageSnapshot snapshot = _statusProvider();
        ConnectionStatusText.Text = UsageStatusFormatter.FormatClaudeConnection(snapshot);
        ConnectionStatusText.Foreground = snapshot.Availability switch
        {
            UsageAvailability.Available => Brushes.ForestGreen,
            UsageAvailability.Error or UsageAvailability.Unavailable or UsageAvailability.Stale => Brushes.OrangeRed,
            _ => Brushes.DodgerBlue,
        };
    }

    internal async Task CopyClaudeCommandAsync()
    {
        IClipboard? clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            ActionMessageText.Text = "クリップボードを利用できません。コマンドを選択してコピーしてください。";
            return;
        }
        await clipboard.SetTextAsync(ClaudeCommandBox.Text ?? string.Empty);
        ActionMessageText.Text = "Claude Code CLIの起動コマンドをコピーしました。";
    }

    internal void OpenSettingsFolder()
    {
        if (_shellOpener is null || _settingsFolder.Length == 0)
        {
            ActionMessageText.Text = "専用フォルダーを開く機能は、アプリ実行中に利用できます。";
            return;
        }
        try
        {
            Directory.CreateDirectory(_settingsFolder);
            _shellOpener.OpenFolder(_settingsFolder);
            ActionMessageText.Text = "監視アプリ専用フォルダーを開きました。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            ActionMessageText.Text = $"設定フォルダーを開けませんでした: {exception.Message}";
        }
    }

    internal ReadmeSectionWindow? OpenLlmSetupReadme()
    {
        try
        {
            string markdown = File.ReadAllText(_readmePath);
            string section = MarkdownSectionReader.Read(markdown, LlmSetupReadmeHeading);
            var window = new ReadmeSectionWindow(section, _readmePath);
            if (IsVisible) _ = window.ShowDialog(this);
            ActionMessageText.Text = "READMEのLLMセットアップ支援案内を開きました。";
            return window;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ActionMessageText.Text = $"READMEの案内を開けませんでした: {exception.Message}";
            return null;
        }
    }

    internal async Task TestConnectionAsync()
    {
        if (_refresh is null)
        {
            ActionMessageText.Text = "接続テストはアプリ実行中に利用できます。";
            return;
        }
        TestConnectionButton.IsEnabled = false;
        ActionMessageText.Text = "接続を確認しています…";
        try
        {
            UsageSnapshot result = await _refresh();
            RefreshStatus();
            ActionMessageText.Text = result.Availability == UsageAvailability.Available
                ? "現在の利用情報を取得できました。"
                : $"取得できませんでした: {result.Reason ?? result.Availability.ToString()}";
            if (result.Availability == UsageAvailability.Available)
                SetupSucceeded?.Invoke();
        }
        catch (OperationCanceledException)
        {
            ActionMessageText.Text = "接続テストをキャンセルしました。";
        }
        finally
        {
            TestConnectionButton.IsEnabled = true;
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e) => OpenSettingsFolder();

    private async void OnCopyCommandClick(object? sender, RoutedEventArgs e) => await CopyClaudeCommandAsync();

    private void OnLlmSetupLinkClick(object? sender, RoutedEventArgs e) => OpenLlmSetupReadme();

    private async void OnTestConnectionClick(object? sender, RoutedEventArgs e) => await TestConnectionAsync();
}
