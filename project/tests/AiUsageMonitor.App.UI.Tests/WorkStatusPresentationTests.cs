using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.App.UI.Runtime;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.WorkStatus;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace AiUsageMonitor.App.UI.Tests;

public sealed partial class WorkStatusPresentationTests
{
    [Fact]
    public void ExplicitClaudeQuestionKeepsTheNormalInputWaitingTextAfterTwelveHours()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "claude-question", WorkProvider.ClaudeCode, "synthetic-long-wait", "turn",
            WorkEventKind.Waiting, now, true, InputPending: true));
        var view = new WorkStatusViewModel();
        view.Update(model, true, true, null, now.AddHours(12));
        Assert.Equal("入力待ち", view.Rows[1].Status);
        Assert.False(view.Rows[1].HasDetail);
        model.Apply(new(1, "claude-answer", WorkProvider.ClaudeCode, "synthetic-long-wait", "turn",
            WorkEventKind.Activity, now.AddHours(12), true, InputPending: false));
        view.Update(model, true, true, null, now.AddHours(12));
        Assert.Equal("作業中 1", view.Rows[1].Status);
    }
    [Fact]
    public void PendingCodexQuestionIsWaitingAndExcludedFromWorkingCount()
    {
        var model = new WorkStatusModel();
        var now = DateTimeOffset.UtcNow;
        for (int index = 0; index < 4; index++)
            model.Apply(new(1, "start-" + index, WorkProvider.CodexDesktop, "s" + index, "t", WorkEventKind.Started, now, true));
        model.Apply(new(1, "question", WorkProvider.CodexDesktop, "s0", "t", WorkEventKind.QuestionOpened,
            now.AddSeconds(1), true, QuestionIds: ["0123456789ABCDEF01234567"]));
        var view = new WorkStatusViewModel();
        view.Update(model, true, true, null, now.AddSeconds(2));
        Assert.Equal("作業中 3 / 入力待ち", view.Rows[0].Status);
        model.Apply(new(1, "stop", WorkProvider.CodexDesktop, "s0", "t", WorkEventKind.StopCandidate, now.AddSeconds(3), true));
        view.Update(model, true, true, null, now.AddSeconds(4));
        Assert.Equal("作業中 3 / 入力待ち", view.Rows[0].Status);
        Assert.Equal("—", view.Rows[1].Status);
    }

    private static readonly string[] SurfaceNames = ["Codex Desktop", "Claude Code"];
    [Fact]
    public void WorkStatusIsIndependentOfUsageAndHasNoRemovedSurfaces()
    {
        var usage = new UsageViewModel { ShowCodex = false, ShowClaude = false };
        var model = new WorkStatusModel();
        usage.WorkStatus.Update(model, true, true, null, DateTimeOffset.UtcNow);
        Assert.True(usage.WorkStatus.IsVisible);
        Assert.Equal(SurfaceNames, usage.WorkStatus.Rows.Select(r => r.Name));
        Assert.All(usage.WorkStatus.Rows, row =>
        {
            Assert.Equal("—", row.Status);
            Assert.False(row.HasDetail);
        });
    }

    [Theory]
    [InlineData(WorkEventKind.Started, "作業中 1")]
    [InlineData(WorkEventKind.Waiting, "入力待ち")]
    [InlineData(WorkEventKind.Completed, "応答終了")]
    [InlineData(WorkEventKind.StopCandidate, "応答終了")]
    [InlineData(WorkEventKind.Failed, "—")]
    [InlineData(WorkEventKind.Interrupted, "—")]
    [InlineData(WorkEventKind.Ended, "—")]
    public void NormalRowsUseCurrentResponseStateWithoutNotificationCounts(WorkEventKind kind, string text)
    {
        var model = new WorkStatusModel();
        model.Apply(new(1, "event", WorkProvider.CodexDesktop, "session", "turn", kind, DateTimeOffset.UtcNow, true));
        var view = new WorkStatusViewModel();
        view.Update(model, true, false, null, DateTimeOffset.UtcNow);
        Assert.Equal(text, view.Rows[0].Status);
        Assert.False(view.Rows[1].IsVisible);
        if (kind == WorkEventKind.StopCandidate) Assert.Equal(WorkSessionState.StopCandidate, Assert.Single(model.Sessions).State);
    }

    [Fact]
    public void ArchivedNoticesStayOutOfNormalStatusAndRemainManageable()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "interrupted", WorkProvider.CodexDesktop, "s", "t", WorkEventKind.Interrupted, now, true));
        model.Sessions.Add(new(WorkProvider.CodexDesktop, "old", "t", WorkSessionState.Unknown, now, 1));
        var view = new WorkStatusViewModel();
        view.Update(model, true, false, null, now);
        var row = view.Rows[0];
        Assert.Equal("—", row.Status);
        Assert.Equal(2, row.NoticeIds.Count);
        row.AcknowledgeRequested += model.Acknowledge;
        row.AcknowledgeCommand.Execute(null);
        view.Update(model, true, false, null, now);
        Assert.Equal("—", row.Status);
        Assert.False(row.HasNotices);
    }

    [Theory]
    [InlineData(WorkProvider.CodexDesktop, 3)]
    [InlineData(WorkProvider.CodexDesktop, 4)]
    [InlineData(WorkProvider.ClaudeCode, 3)]
    [InlineData(WorkProvider.ClaudeCode, 4)]
    public void OldNoticesDoNotOverrideAnActiveTurnAndAcknowledgementDoesNotChangeLiveStatus(WorkProvider provider, int running)
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "completed", provider, "old", "t", WorkEventKind.Completed, now.AddMinutes(-2), true));
        model.Apply(new(1, "interrupted", provider, "interrupted", "t", WorkEventKind.Interrupted, now.AddMinutes(-1), true));
        for (int i = 0; i < running; i++)
            model.Apply(new(1, "started-" + i, provider, "current-" + i, "t", WorkEventKind.Started, now, true));
        var view = new WorkStatusViewModel();
        view.Update(model, true, true, null, now);
        var row = view.Rows.Single(r => r.Provider == provider);
        Assert.Equal($"作業中 {running}", row.Status);
        Assert.Equal(2, row.NoticeIds.Count);
        model.Acknowledge(row.NoticeIds);
        view.Update(model, true, true, null, now);
        Assert.Equal($"作業中 {running}", row.Status);
    }

    [Fact]
    public void ResponseEndUsesTheLatestSessionAndDoesNotDependOnUnreadNotices()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "completed", WorkProvider.CodexDesktop, "old", "t", WorkEventKind.Completed, now.AddMinutes(-2), true));
        model.Apply(new(1, "stopped", WorkProvider.CodexDesktop, "current", "t", WorkEventKind.StopCandidate, now, true));
        var view = new WorkStatusViewModel();
        view.Update(model, true, false, null, now);
        Assert.Equal("応答終了", view.Rows[0].Status);
        model.Acknowledge(view.Rows[0].NoticeIds);
        view.Update(model, true, false, null, now);
        Assert.Equal("応答終了", view.Rows[0].Status);
        model.Apply(new(1, "interrupted", WorkProvider.CodexDesktop, "other", "t", WorkEventKind.Interrupted, now.AddSeconds(1), true));
        view.Update(model, true, false, null, now.AddSeconds(1));
        Assert.Equal("—", view.Rows[0].Status);
    }

    [Fact]
    public void ParallelLiveWorkTakesPriorityOverOldResponseEndAndStaleWorkIsNotShownAsRunning()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "running", WorkProvider.CodexDesktop, "running", "t", WorkEventKind.Started, now, true));
        model.Apply(new(1, "waiting", WorkProvider.CodexDesktop, "waiting", "t", WorkEventKind.Waiting, now, true));
        model.Apply(new(1, "stopped", WorkProvider.CodexDesktop, "stopped", "t", WorkEventKind.StopCandidate, now.AddSeconds(-1), true));
        var view = new WorkStatusViewModel();
        view.Update(model, true, false, null, now);
        Assert.Equal("作業中 1 / 入力待ち", view.Rows[0].Status);
        view.Update(model, true, false, null, now.AddMinutes(11));
        Assert.Equal("—", view.Rows[0].Status);
    }

    [Fact]
    public void NormalRowsHaveNoHookDisclaimerButReadFailuresRemainVisible()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "started", WorkProvider.CodexDesktop, "s", "t", WorkEventKind.Started, now, true));
        var view = new WorkStatusViewModel();
        view.Update(model, true, true, null, now);
        Assert.All(view.Rows, row => Assert.False(row.HasDetail));
        view.Update(model, true, true, "状態を読み込めません", now);
        Assert.True(view.Rows[0].HasDetail);
        Assert.Equal("状態を読み込めません", view.Rows[0].Detail);
    }

    [AvaloniaFact]
    public void WorkOnlyWidgetAndSettingsCanBeConstructedWithoutStartingCollectors()
    {
        var usage = new UsageViewModel { ShowCodex = false, ShowClaude = false };
        var model = new WorkStatusModel();
        var now = DateTimeOffset.UtcNow;
        model.Apply(new(1, "stopped", WorkProvider.CodexDesktop, "s", "t", WorkEventKind.StopCandidate, now, true));
        usage.WorkStatus.Update(model, true, true, null, now);
        var window = new MainWindow { DataContext = usage };
        window.ApplySettings(new AppSettings { ShowCodexUsage = false, ShowCodexWorkStatus = true, ShowClaudeWorkStatus = true }, reposition: false);
        window.Show();
        try
        {
            Assert.True(window.IsVisible);
            Assert.True(usage.WorkStatus.Rows[0].HasNotices);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "確認済み"));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void VisibleWorkRowsUpdateAfterTheWindowHasOpened()
    {
        var usage = new UsageViewModel { ShowCodex = false, ShowClaude = false };
        var window = new MainWindow { DataContext = usage };
        usage.WorkStatus.Update(new WorkStatusModel(), true, true, null, DateTimeOffset.UtcNow);
        window.Show();
        try
        {
            var model = new WorkStatusModel();
            var now = DateTimeOffset.UtcNow;
            for (int index = 0; index < 3; index++)
                model.Apply(new(1, "start-" + index, WorkProvider.CodexDesktop, "s" + index, "t", WorkEventKind.Started, now, true));
            usage.WorkStatus.Update(model, true, true, null, now);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "作業中 3");
        }
        finally { window.Close(); }
    }
    [Fact]
    public async Task CorruptWorkStateStopsOnlyWorkRuntimeAndRetainsOriginal()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory;
        string root = Path.Combine(parent, "work-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "state.json"), "{broken");
        await using var runtime = new WorkStatusRuntime(root, (_, _) => { });
        Assert.NotNull((await runtime.PollAsync(CancellationToken.None)).Problem);
        Assert.Equal("{broken", File.ReadAllText(Path.Combine(root, "state.json")));
    }

    [AvaloniaFact]
    public void ConnectionWindowDoesNotInstallHooksOrStartTheLegacyWidget()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "setup-test", Guid.NewGuid().ToString("N"));
        var window = new WorkStatusSetupWindow(root);
        window.Show();
        try
        {
            Assert.True(window.IsVisible);
            Assert.False(Directory.Exists(root));
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task RuntimeKeepsUnreadOnFailedAcknowledgement()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory;
        string root = Path.Combine(parent, "work-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var runtime = new WorkStatusRuntime(root, (_, _) => { });
        WorkStatusCollector.WriteEvent(root, new(1, "test-complete", WorkProvider.CodexDesktop, "session", "turn",
            WorkEventKind.Completed, DateTimeOffset.UtcNow, true));
        var current = await runtime.PollAsync(CancellationToken.None);
        string id = Assert.Single(current.Model.Notices).Id;
        File.Move(Path.Combine(root, "state.json"), Path.Combine(root, "retained-state.json"));
        Directory.CreateDirectory(Path.Combine(root, "state.json"));
        var failed = await runtime.AcknowledgeAsync([id]);
        Assert.NotNull(failed.Problem);
        Assert.Equal(id, Assert.Single(failed.Model.Notices).Id);
        Assert.True(File.Exists(Path.Combine(root, "retained-state.json")));
    }

    [Fact]
    public async Task MalformedLegacyFieldsDoNotEscapeTheRuntimeOrChangeOriginal()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory;
        string root = Path.Combine(parent, "bad-import-runtime", Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "old");
        Directory.CreateDirectory(legacy);
        const string text = """{"Version":1,"Sessions":[],"Notices":{"CodexDesktop":[{"Id":"e","SessionId":"s","TurnId":"t","State":"Completed","At":"not-a-date"}]}}""";
        File.WriteAllText(Path.Combine(legacy, "state.json"), text);
        await using var runtime = new WorkStatusRuntime(Path.Combine(root, "new"), (_, _) => { });
        Assert.False(await runtime.ImportLegacyAsync(legacy));
        Assert.Empty(runtime.Current.Model.Notices);
        Assert.Equal(text, File.ReadAllText(Path.Combine(legacy, "state.json")));
    }
}
