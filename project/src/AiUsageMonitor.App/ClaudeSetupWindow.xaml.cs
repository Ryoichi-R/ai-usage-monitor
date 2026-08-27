using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using AiUsageMonitor.Claude.Windows.Process;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Platform.Windows;

namespace AiUsageMonitor.App;

public partial class ClaudeSetupWindow : Window
{
    private const string LlmSetupReadmeHeading = "LLMによるセットアップ支援（非推奨）";

    public event Action? SetupSucceeded;
    private readonly Func<UsageSnapshot> _statusProvider;
    private readonly DispatcherTimer _statusTimer;
    private readonly string _settingsFolder;
    private readonly Func<Task<UsageSnapshot>>? _refresh;

    public ClaudeSetupWindow(
        Func<UsageSnapshot> statusProvider,
        Func<Task<UsageSnapshot>>? refresh = null,
        string? executablePath = null)
    {
        InitializeComponent();
        _statusProvider = statusProvider;
        _refresh = refresh;
        var workspace = new ClaudeWorkspaceProvisioner(new WindowsAppPathProvider());
        _settingsFolder = workspace.EnsureWorkspace();
        SettingsFolderBox.Text = _settingsFolder;
        string command = string.IsNullOrWhiteSpace(executablePath) ? "claude" : executablePath;
        ClaudeCommandBox.Text =
            $"Set-Location -LiteralPath '{EscapePowerShell(_settingsFolder)}'; & '{EscapePowerShell(command)}' --setting-sources '' --tools '' --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader";
        _statusTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => RefreshStatus(),
            Dispatcher);
        Loaded += (_, _) => RefreshStatus();
        Closed += (_, _) => _statusTimer.Stop();
        _statusTimer.Start();
    }

    internal void RefreshStatus()
    {
        UsageSnapshot snapshot = _statusProvider();
        ConnectionStatusText.Text = UsageStatusFormatter.FormatClaudeConnection(snapshot);
        ConnectionStatusText.Foreground = snapshot.Availability switch
        {
            UsageAvailability.Available => System.Windows.Media.Brushes.ForestGreen,
            UsageAvailability.Error or UsageAvailability.Unavailable or UsageAvailability.Stale => System.Windows.Media.Brushes.OrangeRed,
            _ => System.Windows.Media.Brushes.DodgerBlue,
        };
    }

    private void CopyClaudeCommand(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(ClaudeCommandBox.Text);
        ActionMessageText.Text = "Claude Code CLIの起動コマンドをコピーしました。";
    }

    private void OpenSettingsFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settingsFolder);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(_settingsFolder);
            Process.Start(startInfo);
            ActionMessageText.Text = "監視アプリ専用フォルダーを開きました。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ActionMessageText.Text = $"設定フォルダーを開けませんでした: {exception.Message}";
        }
    }

    private void OpenLlmSetupReadme(object sender, RoutedEventArgs e)
    {
        try
        {
            string readmePath = Path.Combine(AppContext.BaseDirectory, "README.md");
            string markdown = File.ReadAllText(readmePath);
            string section = MarkdownSectionReader.Read(markdown, LlmSetupReadmeHeading);
            var window = new ReadmeSectionWindow(section, readmePath)
            {
                Owner = this,
            };
            window.ShowDialog();
            ActionMessageText.Text = "READMEのLLMセットアップ支援案内を開きました。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ActionMessageText.Text = $"READMEの案内を開けませんでした: {exception.Message}";
        }
    }

    private async void TestConnection(object sender, RoutedEventArgs e)
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

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
