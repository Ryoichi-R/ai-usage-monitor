using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.WorkStatus;

namespace AiUsageMonitor.WorkStatus.Tests;

public sealed class WorkStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 5, 0, 0, TimeSpan.Zero);
    private static WorkStatusEvent Event(WorkEventKind kind, int tick = 0, string session = "s", string turn = "t") =>
        new(1, $"{kind}-{session}-{turn}-{tick}", WorkProvider.CodexDesktop, session, turn, kind, Now.AddSeconds(tick), true, tick);
    private static string Fixture()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory;
        string path = Path.Combine(parent, "work-status-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData(WorkEventKind.Connected, WorkSessionState.Idle)]
    [InlineData(WorkEventKind.Started, WorkSessionState.Running)]
    [InlineData(WorkEventKind.Waiting, WorkSessionState.Waiting)]
    [InlineData(WorkEventKind.StopCandidate, WorkSessionState.StopCandidate)]
    [InlineData(WorkEventKind.Completed, WorkSessionState.Completed)]
    [InlineData(WorkEventKind.Failed, WorkSessionState.Failed)]
    [InlineData(WorkEventKind.Interrupted, WorkSessionState.Interrupted)]
    [InlineData(WorkEventKind.Ended, WorkSessionState.Idle)]
    public void ExplicitEventsHaveSeparateStates(WorkEventKind kind, WorkSessionState state)
    {
        var model = new WorkStatusModel();
        Assert.True(model.Apply(Event(kind)));
        Assert.Equal(state, Assert.Single(model.Sessions).State);
    }

    [Fact]
    public void OldTerminalCannotStopNewTurn()
    {
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.Started, 0));
        model.Apply(Event(WorkEventKind.Started, 1, turn: "new"));
        model.Apply(Event(WorkEventKind.Completed, 2));
        Assert.Equal(WorkSessionState.Running, Assert.Single(model.Sessions).State);
        Assert.Empty(model.Notices);
    }

    [Fact]
    public void NewTaskAndSourceFailureKeepUnreadCompletion()
    {
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.Completed));
        model.Apply(Event(WorkEventKind.Started, 1, turn: "new"));
        WorkStatusSummary summary = model.Summarize(WorkProvider.CodexDesktop, Now.AddMinutes(12), false);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, summary.Unknown);
        Assert.Equal(0, summary.Running);
    }

    [Fact]
    public void AcknowledgementDoesNotConsumeConcurrentArrivalOrReplay()
    {
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.Completed));
        string[] ids = [.. model.Notices.Select(n => n.Id)];
        model.Apply(Event(WorkEventKind.Completed, 1, session: "new"));
        model.Acknowledge(ids);
        Assert.Equal("new", Assert.Single(model.Notices).SessionId);
        model.Apply(Event(WorkEventKind.Completed) with { Id = "replayed" });
        Assert.Single(model.Notices);
    }

    [Fact]
    public void ClosedSessionDoesNotCreateAnUnknownCountOrRemoveUnreadInterruption()
    {
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.Interrupted));
        model.Apply(Event(WorkEventKind.Ended, 1));
        WorkStatusSummary summary = model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(2), true);
        Assert.Equal(0, summary.Unknown);
        Assert.Equal(1, summary.Interrupted);
        Assert.Equal(0, summary.Failed);
        model.Acknowledge(summary.Notices.Select(n => n.Id));
        Assert.Empty(model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(3), true).Notices);
    }

    [Fact]
    public void LegacyUnknownCanBeAcknowledgedDurablyWithoutDeletingTheSession()
    {
        string root = Fixture();
        var model = new WorkStatusModel();
        model.Sessions.Add(new(WorkProvider.CodexDesktop, "legacy", "turn", WorkSessionState.Unknown, Now, 1));
        var store = new WorkStatusStore(root);
        store.Save(model);
        var collector = new WorkStatusCollector(store);
        WorkNotice notice = Assert.Single(collector.Current.Model.Summarize(WorkProvider.CodexDesktop, Now, true).Notices);
        collector.Acknowledge([notice.Id]);
        WorkStatusModel restarted = store.Load();
        Assert.Single(restarted.Sessions);
        Assert.Equal(0, restarted.Summarize(WorkProvider.CodexDesktop, Now.AddDays(1), true).Unknown);
    }

    [Fact]
    public void AcknowledgingOldUnknownDoesNotConsumeANewerGeneration()
    {
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.Activity));
        string oldId = Assert.Single(model.Summarize(WorkProvider.CodexDesktop, Now, true).Notices).Id;
        model.Apply(Event(WorkEventKind.Started, 1, turn: "new"));
        model.Acknowledge([oldId]);
        WorkStatusSummary summary = model.Summarize(WorkProvider.CodexDesktop, Now.AddMinutes(12), true);
        Assert.Equal(1, summary.Unknown);
        Assert.NotEqual(oldId, Assert.Single(summary.Notices).Id);
    }

    [Fact]
    public void AcknowledgementSnapshotLeavesNewUnknownAndOtherProviderUnread()
    {
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.Activity));
        string[] ids = [.. model.Summarize(WorkProvider.CodexDesktop, Now, true).Notices.Select(n => n.Id)];
        model.Apply(Event(WorkEventKind.Activity, 1, session: "other"));
        model.Apply(Event(WorkEventKind.Interrupted, 2) with { Provider = WorkProvider.ClaudeCode });
        model.Acknowledge(ids);
        Assert.Equal("other", Assert.Single(model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(3), true).Notices).SessionId);
        Assert.Equal(1, model.Summarize(WorkProvider.ClaudeCode, Now.AddSeconds(3), true).Interrupted);
    }

    [Fact]
    public void FailedUnknownAcknowledgementDoesNotHideItsPendingStatus()
    {
        string root = Fixture();
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.Activity));
        var store = new WorkStatusStore(root);
        store.Save(model);
        var collector = new WorkStatusCollector(store);
        string id = Assert.Single(model.Summarize(WorkProvider.CodexDesktop, Now, true).Notices).Id;
        File.Move(Path.Combine(root, "state.json"), Path.Combine(root, "retained-state.json"));
        Directory.CreateDirectory(Path.Combine(root, "state.json"));
        Exception? error = Record.Exception(() => collector.Acknowledge([id]));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(1, collector.Current.Model.Summarize(WorkProvider.CodexDesktop, Now, true).Unknown);
    }

    [Fact]
    public void CandidateIsRetractedOnActualContinuation()
    {
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.StopCandidate));
        model.Apply(Event(WorkEventKind.Activity, 1));
        Assert.Empty(model.Notices);
        Assert.Equal(WorkSessionState.Running, Assert.Single(model.Sessions).State);
    }

    [Fact]
    public void OnlyStopDerivedCompletionMayBeRetracted()
    {
        var model = new WorkStatusModel();
        model.Apply(Event(WorkEventKind.Completed) with { CompletionRetractable = true });
        model.Apply(Event(WorkEventKind.Activity, 1));
        Assert.Empty(model.Notices);
        model.Apply(Event(WorkEventKind.Completed, 2));
        model.Apply(Event(WorkEventKind.Activity, 3));
        Assert.Single(model.Notices);
    }

    [Fact]
    public void InvalidOriginsVersionsAndIdentifiersAreIgnored()
    {
        var model = new WorkStatusModel();
        Assert.False(model.Apply(Event(WorkEventKind.Completed) with { DesktopVerified = false }));
        Assert.False(model.Apply(Event(WorkEventKind.Completed) with { Protocol = 2 }));
        Assert.False(model.Apply(Event(WorkEventKind.Completed) with { Provider = (WorkProvider)99 }));
        Assert.False(model.Apply(Event(WorkEventKind.Completed) with { SessionId = "" }));
        Assert.Empty(model.Sessions);
    }

    [Theory]
    [InlineData("SessionStart", WorkEventKind.Connected)]
    [InlineData("UserPromptSubmit", WorkEventKind.Started)]
    [InlineData("PreToolUse", WorkEventKind.Activity)]
    [InlineData("PostToolUse", WorkEventKind.Activity)]
    [InlineData("PermissionRequest", WorkEventKind.Waiting)]
    [InlineData("Stop", WorkEventKind.StopCandidate)]
    [InlineData("StopFailure", WorkEventKind.Failed)]
    [InlineData("Interrupt", WorkEventKind.Interrupted)]
    [InlineData("SessionEnd", WorkEventKind.Ended)]
    public void NormalizerDiscardsPrivateInput(string name, WorkEventKind expected)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            session_id = "synthetic-session",
            turn_id = "synthetic-turn",
            hook_event_name = name,
            prompt = "synthetic-private-prompt",
            last_assistant_message = "synthetic-private-reply",
            tool_input = "synthetic-private-input",
            transcript_path = "synthetic-private-path",
        }));
        WorkStatusEvent value = HookNormalizer.Normalize(json.RootElement, WorkProvider.ClaudeCode, true, Now)!;
        Assert.Equal(expected, value.Kind);
        string text = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("synthetic-", text);
        Assert.DoesNotContain("prompt", text);
    }

    [Theory]
    [InlineData("SubagentStop")]
    [InlineData("TaskCompleted")]
    [InlineData("PostModelRequest")]
    [InlineData("unknown")]
    public void AuxiliaryAndRequestOutcomesAreNotTurnCompletion(string name)
    {
        using var json = JsonDocument.Parse($$"""{"session_id":"s","hook_event_name":"{{name}}"}""");
        Assert.Null(HookNormalizer.Normalize(json.RootElement, WorkProvider.CodexDesktop, true, Now));
    }

    [Theory]
    [InlineData("idle_prompt")]
    [InlineData("agent_completed")]
    public void IdleAndAgentNotificationsDoNotProveMainCompletion(string notification)
    {
        using var json = JsonDocument.Parse($$"""{"session_id":"s","hook_event_name":"Notification","notification_type":"{{notification}}"}""");
        Assert.Null(HookNormalizer.Normalize(json.RootElement, WorkProvider.ClaudeCode, true, Now));
    }

    [Fact]
    public void StopConfirmationRequiresKnownInactiveHookAndExplicitEvidence()
    {
        using var missing = JsonDocument.Parse("""{"session_id":"s","hook_event_name":"Stop"}""");
        using var inactive = JsonDocument.Parse("""{"session_id":"s","hook_event_name":"Stop","stop_hook_active":false}""");
        using var active = JsonDocument.Parse("""{"session_id":"s","hook_event_name":"Stop","stop_hook_active":true}""");
        Assert.Equal(WorkEventKind.StopCandidate, HookNormalizer.Normalize(missing.RootElement, WorkProvider.ClaudeCode, true, Now, true)!.Kind);
        Assert.Equal(WorkEventKind.Completed, HookNormalizer.Normalize(inactive.RootElement, WorkProvider.ClaudeCode, true, Now, true)!.Kind);
        Assert.Equal(WorkEventKind.StopCandidate, HookNormalizer.Normalize(active.RootElement, WorkProvider.ClaudeCode, true, Now, true)!.Kind);
        Assert.Null(HookNormalizer.Normalize(inactive.RootElement, WorkProvider.ClaudeCode, false, Now));
    }

    [Fact]
    public void CollectorCommitsAndAcknowledgesBeforeDeletingSpool()
    {
        string root = Fixture();
        var store = new WorkStatusStore(root);
        var collector = new WorkStatusCollector(store);
        WorkStatusCollector.WriteEvent(root, Event(WorkEventKind.Completed));
        WorkStatusSnapshot value = collector.Poll(Now);
        WorkNotice notice = Assert.Single(value.Model.Notices);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "events")));
        collector.Acknowledge([notice.Id]);
        var restarted = new WorkStatusCollector(store);
        WorkStatusCollector.WriteEvent(root, Event(WorkEventKind.Completed));
        Assert.Empty(restarted.Poll(Now).Model.Notices);
    }

    [Fact]
    public void FailedCommitKeepsSpoolAndDoesNotPublishCandidate()
    {
        string root = Fixture();
        var collector = new WorkStatusCollector(new(root));
        WorkStatusCollector.WriteEvent(root, Event(WorkEventKind.Completed));
        Directory.CreateDirectory(Path.Combine(root, "state.json"));
        Exception? error = Record.Exception(() => collector.Poll(Now));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(collector.Current.Model.Notices);
        Assert.Single(Directory.GetFiles(Path.Combine(root, "events")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"Version\":99}")]
    [InlineData("{\"Sessions\":null}")]
    public void CorruptStateIsNotReplacedWithEmptyState(string text)
    {
        string root = Fixture(), path = Path.Combine(root, "state.json");
        File.WriteAllText(path, text);
        Assert.ThrowsAny<Exception>(() => new WorkStatusStore(root).Load());
        Assert.Equal(text, File.ReadAllText(path));
    }

    [Fact]
    public void UnknownProtocolRemainsInSpoolAndDoesNotBlockValidProvider()
    {
        string root = Fixture();
        WorkStatusCollector.WriteEvent(root, Event(WorkEventKind.Completed));
        File.WriteAllText(Path.Combine(root, "events", "future.json"), JsonSerializer.Serialize(Event(WorkEventKind.Failed) with { Protocol = 99 }));
        WorkStatusSnapshot value = new WorkStatusCollector(new(root)).Poll(Now);
        Assert.Single(value.Model.Notices);
        Assert.Equal("WORK_STATUS_EVENT_INVALID", value.Problem);
        Assert.True(File.Exists(Path.Combine(root, "events", "future.json")));
    }

    [Fact]
    public void OutsideAndSiblingPathsAreRejected()
    {
        string root = Fixture();
        Assert.Throws<IOException>(() => WorkStatusPaths.Inside(root, root + "-sibling/file"));
        Assert.Throws<IOException>(() => WorkStatusPaths.Inside(root, Path.Combine(root, "..", "outside")));
        Assert.Equal(Path.Combine(root, "日本語 space"), WorkStatusPaths.Inside(root, Path.Combine(root, "日本語 space")));
    }
}
