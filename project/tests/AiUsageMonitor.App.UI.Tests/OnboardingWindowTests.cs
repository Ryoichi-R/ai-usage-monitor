using AiUsageMonitor.App.UI.Onboarding;
using AiUsageMonitor.App.UI.Settings;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace AiUsageMonitor.App.UI.Tests;

// WPF版OnboardingTests・MonitorChoiceTestsの意図をAvaloniaへ移植したもの。
public sealed class OnboardingWindowTests
{
    [Fact]
    public void ClaudeSetupStatusIsJapaneseAndActionable()
    {
        string status = UsageStatusFormatter.Format(
            "CLAUDE",
            UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with { Availability = UsageAvailability.Setup });
        string location = OperatingSystem.IsMacOS() ? "メニューバー" : "通知領域";
        Assert.Equal($"未接続 — {location}から連携設定を開いてください", status);
        Assert.DoesNotContain("SETUP", status, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ClaudeSetupWindowShowsInstructionsAndLiveConnectionState()
    {
        UsageSnapshot current = UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with
        {
            Availability = UsageAvailability.Setup,
        };
        var workspace = new FakeWorkspace("/tmp/ai usage/it's-workspace");
        var window = new ClaudeSetupWindow(() => current, null, null, workspace, null, "README.md");
        try
        {
            Assert.Contains("未接続", window.ConnectionStatusText.Text, StringComparison.Ordinal);
            Assert.Equal(workspace.WorkspacePath, window.SettingsFolderBox.Text);
            Assert.Equal(1, workspace.EnsureCount);
            Assert.Contains("--setting-sources ''", window.ClaudeCommandBox.Text, StringComparison.Ordinal);
            Assert.Contains("--strict-mcp-config", window.ClaudeCommandBox.Text, StringComparison.Ordinal);
            Assert.Contains("代行しません", window.DesktopLimitationText.Text, StringComparison.Ordinal);
            Assert.Contains("/usageだけ", window.PromptInstructionText.Text, StringComparison.Ordinal);
            Assert.Equal("非推奨", window.NonRecommendedLabel.Text);
            Assert.Equal("LLMに任せてセットアップする際の案内はこちら", window.LlmSetupSupportLink.Content);

            current = current with { Availability = UsageAvailability.Available };
            window.RefreshStatus();
            Assert.Equal("Claude Codeと接続しました。", window.ConnectionStatusText.Text);
            Assert.Same(Brushes.ForestGreen, window.ConnectionStatusText.Foreground);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClaudeSetupWindowShowsWaitingAfterSetupBeforeFirstObservation()
    {
        UsageSnapshot waiting = UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with
        {
            Availability = UsageAvailability.Waiting,
        };
        var window = new ClaudeSetupWindow(() => waiting, null, null, null, null, "README.md");
        try
        {
            Assert.Equal("接続済みです。最初の利用情報を待っています。", window.ConnectionStatusText.Text);
            Assert.Same(Brushes.DodgerBlue, window.ConnectionStatusText.Foreground);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(UsageAvailability.Available, "現在の利用情報を取得できました。", true)]
    [InlineData(UsageAvailability.Error, "取得できませんでした", false)]
    public async Task ClaudeSetupWindowConnectionTestReportsResultAndSignalsSuccess(
        UsageAvailability availability,
        string expectedMessage,
        bool expectedSuccess)
    {
        UsageSnapshot result = UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with
        {
            Availability = availability,
        };
        var window = new ClaudeSetupWindow(() => result, () => Task.FromResult(result), null, null, null, "README.md");
        bool succeeded = false;
        window.SetupSucceeded += () => succeeded = true;
        try
        {
            await window.TestConnectionAsync();
            Assert.Contains(expectedMessage, window.ActionMessageText.Text, StringComparison.Ordinal);
            Assert.Equal(expectedSuccess, succeeded);
            Assert.True(window.TestConnectionButton.IsEnabled);
            Assert.Same(
                availability == UsageAvailability.Available ? Brushes.ForestGreen : Brushes.OrangeRed,
                window.ConnectionStatusText.Foreground);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClaudeSetupWindowWithoutRefreshExplainsUnavailableTestAndReportsCancel()
    {
        UsageSnapshot status = UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow);
        var withoutRefresh = new ClaudeSetupWindow(() => status, null, null, null, null, "README.md");
        var canceled = new ClaudeSetupWindow(
            () => status,
            () => Task.FromCanceled<UsageSnapshot>(new CancellationToken(canceled: true)),
            null,
            null,
            null,
            "README.md");
        try
        {
            await withoutRefresh.TestConnectionAsync();
            Assert.Contains("アプリ実行中", withoutRefresh.ActionMessageText.Text, StringComparison.Ordinal);
            await canceled.TestConnectionAsync();
            Assert.Contains("キャンセル", canceled.ActionMessageText.Text, StringComparison.Ordinal);
            Assert.True(canceled.TestConnectionButton.IsEnabled);
        }
        finally
        {
            withoutRefresh.Close();
            canceled.Close();
        }
    }

    [AvaloniaFact]
    public void ClaudeSetupWindowOpensTheWorkspaceFolderOnlyWhenAHostProvidesIt()
    {
        var workspace = new FakeWorkspace(Path.Combine(Path.GetTempPath(), "ai-usage-onboarding-" + Guid.NewGuid().ToString("N")));
        var opener = new FakeShellOpener();
        var window = new ClaudeSetupWindow(
            () => UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow),
            null,
            "/opt/claude",
            workspace,
            opener,
            "README.md");
        var unavailable = new ClaudeSetupWindow(
            () => UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow),
            null,
            null,
            null,
            null,
            "README.md");
        try
        {
            window.OpenSettingsFolder();
            Assert.Equal([workspace.WorkspacePath], opener.Opened);
            Assert.Contains("開きました", window.ActionMessageText.Text, StringComparison.Ordinal);
            Assert.Contains("'/opt/claude'", window.ClaudeCommandBox.Text, StringComparison.Ordinal);

            opener.Failure = new InvalidOperationException("no shell");
            window.OpenSettingsFolder();
            Assert.Contains("開けませんでした: no shell", window.ActionMessageText.Text, StringComparison.Ordinal);

            unavailable.OpenSettingsFolder();
            Assert.Contains("アプリ実行中", unavailable.ActionMessageText.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            unavailable.Close();
            Directory.Delete(workspace.WorkspacePath, recursive: true);
        }
    }

    [AvaloniaFact]
    public void LlmSetupLinkShowsTheReadmeSectionOrExplainsAMissingReadme()
    {
        string readme = Path.Combine(Path.GetTempPath(), "ai-usage-readme-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(readme, "# Title\n\n#### LLMによるセットアップ支援（非推奨）\n\n信頼確認を自動操作しない。\n\n#### 次の節\n\n対象外\n");
        UsageSnapshot status = UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow);
        var window = new ClaudeSetupWindow(() => status, null, null, null, null, readme);
        var missing = new ClaudeSetupWindow(() => status, null, null, null, null, readme + ".missing");
        try
        {
            ReadmeSectionWindow section = Assert.IsType<ReadmeSectionWindow>(window.OpenLlmSetupReadme());
            Assert.StartsWith("#### LLMによるセットアップ支援（非推奨）", section.ReadmeSectionBox.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("次の節", section.ReadmeSectionBox.Text, StringComparison.Ordinal);
            Assert.Contains("開きました", window.ActionMessageText.Text, StringComparison.Ordinal);
            section.Close();

            Assert.Null(missing.OpenLlmSetupReadme());
            Assert.Contains("開けませんでした", missing.ActionMessageText.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            missing.Close();
            File.Delete(readme);
        }
    }

    [Fact]
    public void MarkdownSectionReaderReturnsOnlyRequestedSection()
    {
        string markdown = "# Top\n\n#### LLMによるセットアップ支援（非推奨）\n\n信頼確認を自動操作しない。\n\n```\n# not a heading\n```\n\n##### 子見出し\n\n本文\n\n#### 次の節\n\n対象外\n";

        string section = MarkdownSectionReader.Read(markdown, "LLMによるセットアップ支援（非推奨）");

        Assert.StartsWith("#### LLMによるセットアップ支援（非推奨）", section, StringComparison.Ordinal);
        Assert.Contains("信頼確認を自動操作しない", section, StringComparison.Ordinal);
        Assert.Contains("# not a heading", section, StringComparison.Ordinal);
        Assert.Contains("子見出し", section, StringComparison.Ordinal);
        Assert.DoesNotContain("次の節", section, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => MarkdownSectionReader.Read(markdown, "存在しない見出し"));
    }

    [AvaloniaFact]
    public void WelcomeWindowRecordsTheChosenStartMode()
    {
        var claude = new WelcomeWindow();
        claude.Show();
        claude.StartWithClaude();
        Assert.True(claude.Completed);
        Assert.True(claude.UseClaude);

        var codex = new WelcomeWindow();
        codex.Show();
        codex.StartCodexOnly();
        Assert.True(codex.Completed);
        Assert.False(codex.UseClaude);

        var dismissed = new WelcomeWindow();
        dismissed.Show();
        dismissed.Close();
        Assert.False(dismissed.Completed);
    }

    [AvaloniaFact]
    public void ReadmeSectionWindowShowsSectionAndExplainsMissingFullReadme()
    {
        var window = new ReadmeSectionWindow("テスト用の案内", Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N") + ".md"));
        try
        {
            Assert.Equal("テスト用の案内", window.ReadmeSectionBox.Text);
            window.OpenFullReadme();
            Assert.Contains("開けませんでした", window.OpenReadmeMessageText.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [Theory]
    [InlineData("/Users/a/ws", null, false, "cd '/Users/a/ws' && 'claude' --setting-sources ''")]
    [InlineData("/Users/a/it's", "/opt/c laude", false, "cd '/Users/a/it'\\''s' && '/opt/c laude' --setting-sources ''")]
    [InlineData(@"C:\ws\it's", null, true, @"Set-Location -LiteralPath 'C:\ws\it''s'; & 'claude' --setting-sources ''")]
    public void ClaudeTrustCommandQuotesForTheTargetShell(string folder, string? executable, bool powerShell, string expectedPrefix)
    {
        string command = ClaudeTrustCommand.Create(folder, executable, powerShell);
        Assert.StartsWith(expectedPrefix, command, StringComparison.Ordinal);
        Assert.EndsWith(ClaudeTrustCommand.IsolationArguments, command, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorChoiceMatchesStableIdLegacyNameAndPrimary()
    {
        MonitorChoice first = MonitorChoice.ForScreen("DISPLAY2", "stable-a", primary: true);
        MonitorChoice second = MonitorChoice.ForScreen("DISPLAY1", "stable-b", primary: false);
        Assert.Equal("DISPLAY2（プライマリ）", first.DisplayName);
        Assert.Equal("DISPLAY1", second.ToString());

        Assert.Same(second, MonitorChoice.Select([first, second], "DISPLAY9", "stable-b"));
        MonitorChoice disconnected = MonitorChoice.Select([first], "DISPLAY1", "stable-b");
        Assert.True(disconnected.IsDisconnected);
        Assert.Equal(MonitorChoice.DisconnectedDisplayName, disconnected.DisplayName);
        Assert.Same(second, MonitorChoice.Select([first, second], "display1", null));
        Assert.Same(MonitorChoice.Automatic, MonitorChoice.Select([first], "DISPLAY7", null));
        Assert.Same(MonitorChoice.Automatic, MonitorChoice.Select([first], null, null));
    }

    private sealed class FakeWorkspace(string path) : IClaudeWorkspaceProvisioner
    {
        public string WorkspacePath { get; } = path;

        public int EnsureCount { get; private set; }

        public string EnsureWorkspace()
        {
            EnsureCount++;
            return WorkspacePath;
        }
    }

    private sealed class FakeShellOpener : IShellOpener
    {
        public List<string> Opened { get; } = [];

        public Exception? Failure { get; set; }

        public void OpenFolder(string path)
        {
            if (Failure is not null) throw Failure;
            Opened.Add(path);
        }
    }
}
