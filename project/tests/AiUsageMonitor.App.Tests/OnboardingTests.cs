using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Core.Presentation;

namespace AiUsageMonitor.App.Tests;

public sealed class OnboardingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ClaudeSetupStatus_IsJapaneseAndActionable()
    {
        var snapshot = Snapshot(UsageAvailability.Setup);

        string status = UsageStatusFormatter.Format("CLAUDE", snapshot);

        Assert.Equal("未接続 — 通知領域から連携設定を開いてください", status);
        Assert.DoesNotContain("SETUP", status, StringComparison.Ordinal);
    }

    [Fact]
    public void ClaudeSetupWindow_ShowsInstructionsAndLiveConnectionState()
    {
        RunInSta(() =>
        {
            UsageSnapshot current = Snapshot(UsageAvailability.Setup);
            var window = new ClaudeSetupWindow(() => current);
            try
            {
                window.RefreshStatus();
                Assert.Contains("未接続", window.ConnectionStatusText.Text, StringComparison.Ordinal);
                Assert.EndsWith(
                    Path.Combine("CodexUsageMonitor", "ClaudeCliWorkspace"),
                    window.SettingsFolderBox.Text,
                    StringComparison.OrdinalIgnoreCase);
                Assert.Contains("--setting-sources ''", window.ClaudeCommandBox.Text, StringComparison.Ordinal);
                Assert.Contains("--strict-mcp-config", window.ClaudeCommandBox.Text, StringComparison.Ordinal);
                Assert.Contains("代行しません", window.DesktopLimitationText.Text, StringComparison.Ordinal);
                Assert.Contains("/usageだけ", window.PromptInstructionText.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("任意のメッセージ", window.PromptInstructionText.Text, StringComparison.Ordinal);
                Assert.Equal("非推奨", window.NonRecommendedLabel.Text);
                var linkText = Assert.IsType<Run>(window.LlmSetupSupportLink.Inlines.FirstInline);
                Assert.Equal("LLMに任せてセットアップする際の案内はこちら", linkText.Text);

                current = Snapshot(UsageAvailability.Available);
                window.RefreshStatus();
                Assert.Equal("Claude Codeと接続しました。", window.ConnectionStatusText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ClaudeSetupWindow_ShowsWaitingAfterSetupBeforeFirstObservation()
    {
        RunInSta(() =>
        {
            var runtime = new ClaudeUsageRuntime(_ =>
                new FakeClaudeUsageSource(Snapshot(UsageAvailability.Available)));
            runtime.Configure(
                new ClaudeActiveSourceConfiguration(
                    "claude",
                    @"C:\app\claude-statusline-bridge.ps1",
                    TimeSpan.FromSeconds(30)),
                Now);
            UsageSnapshot waiting = runtime.ForDisplay(
                runtime.Current(Now).Snapshot,
                setupCompleted: true);
            var window = new ClaudeSetupWindow(() => waiting);
            try
            {
                window.RefreshStatus();
                Assert.Equal(
                    "接続済みです。最初の利用情報を待っています。",
                    window.ConnectionStatusText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MarkdownSectionReader_ReturnsOnlyRequestedSection()
    {
        const string markdown = """
            # README

            #### 別の案内

            対象外です。

            #### LLMによるセットアップ支援（非推奨）

            このWindows PCでセットアップしてください。

            ```text
            必須条件:
            - 信頼確認を自動操作しない。
            ```

            ### 次の節

            ここは含めません。
            """;

        string section = MarkdownSectionReader.Read(
            markdown,
            "LLMによるセットアップ支援（非推奨）");

        Assert.StartsWith("#### LLMによるセットアップ支援（非推奨）", section, StringComparison.Ordinal);
        Assert.Contains("信頼確認を自動操作しない", section, StringComparison.Ordinal);
        Assert.DoesNotContain("次の節", section, StringComparison.Ordinal);
    }

    [Fact]
    public void WelcomeWindow_ClaudeChoiceContinuesToSetup()
    {
        RunInSta(() =>
        {
            var window = new WelcomeWindow();
            window.Loaded += (_, _) => window.ClaudeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(window.ShowDialog());
            Assert.True(window.UseClaude);
        });
    }

    [Fact]
    public void SettingsWindow_SetupActionEnablesClaudeAndRaisesRequest()
    {
        RunInSta(() =>
        {
            var window = new SettingsWindow(new(), () => new());
            bool requested = false;
            window.ClaudeSetupRequested += () => requested = true;

            window.ClaudeSetupButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(window.ShowClaudeBox.IsChecked);
            Assert.True(requested);
            window.Close();
        });
    }

    [Fact]
    public async Task ClaudeConnectionTest_StartsOfficialCliWhenOngoingModeIsStatusLineOnly()
    {
        var source = new FakeClaudeUsageSource(Snapshot(UsageAvailability.Available));
        var runtime = new ClaudeUsageRuntime(_ => source);
        runtime.Configure(
            new ClaudeActiveSourceConfiguration(
                "claude",
                @"C:\app\claude-statusline-bridge.ps1",
                TimeSpan.FromSeconds(30)),
            Now);

        UsageSnapshot statusLineOnly = await runtime.RefreshAsync(
            ClaudeUsageAcquisitionMode.StatusLineOnly,
            false,
            Now,
            CancellationToken.None);
        UsageSnapshot connectionTest = await AiUsageMonitor.App.App.RunClaudeConnectionTestAsync(
            runtime,
            Now,
            CancellationToken.None);

        Assert.Equal(UsageAvailability.Waiting, statusLineOnly.Availability);
        Assert.Equal(UsageAvailability.Available, connectionTest.Availability);
        Assert.Equal(1, source.CallCount);
    }

    private static UsageSnapshot Snapshot(UsageAvailability availability) =>
        new(
            UsageProvider.Claude,
            Now,
            Now,
            availability,
            null,
            null,
            [],
            null,
            false,
            availability == UsageAvailability.Available ? Now : null);

    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private sealed class FakeClaudeUsageSource(UsageSnapshot snapshot) : IClaudeUsageSource
    {
        public int CallCount { get; private set; }

        public Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new ClaudeUsageObservation(
                ClaudeUsageSourceKind.CliScreen,
                snapshot));
        }
    }
}
