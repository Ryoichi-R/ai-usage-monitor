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

    [Fact]
    public void UnreadableEventKeepsWarningThroughBackoffAndAcknowledgementUntilRepaired()
    {
        string root = Fixture();
        try
        {
            var collector = new WorkStatusCollector(new(root));
            WorkStatusCollector.WriteEvent(root, Event(WorkEventKind.Completed));
            string notice = Assert.Single(collector.PollView(Now).Model.Notices).Id;
            string path = Path.Combine(root, "events", "unreadable.json");
            File.WriteAllText(path, "{");
            Assert.Equal("WORK_STATUS_EVENT_UNREADABLE", collector.PollView(Now).Problem);
            WorkStatusViewSnapshot waiting = collector.PollView(Now.AddSeconds(2));
            Assert.Equal("WORK_STATUS_EVENT_UNREADABLE", waiting.Problem);
            Assert.Null(waiting.UnavailableProviders); // The damaged event cannot be attributed to one provider.
            Assert.Equal("WORK_STATUS_EVENT_UNREADABLE", collector.AcknowledgeView([notice]).Problem);
            Assert.Equal("WORK_STATUS_EVENT_UNREADABLE", collector.CurrentView.Problem);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(Event(WorkEventKind.Started, 3, turn: "next"), WorkStatusStore.JsonOptions));
            Assert.NotNull(collector.PollView(Now.AddSeconds(4)).Problem);
            WorkStatusViewSnapshot repaired = collector.PollView(Now.AddSeconds(30));
            Assert.Null(repaired.Problem);
            Assert.Equal(WorkSessionState.Running, Assert.Single(repaired.Model.Sessions).State);
            Assert.Empty(repaired.Model.Notices);
            Assert.False(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedUnreadableEventClearsWarningBeforeRetryDeadline(bool removeDirectory)
    {
        string root = Fixture();
        try
        {
            var collector = new WorkStatusCollector(new(root));
            string events = Directory.CreateDirectory(Path.Combine(root, "events")).FullName;
            string path = Path.Combine(events, "unreadable.json");
            File.WriteAllText(path, "{");
            Assert.NotNull(collector.PollView(Now).Problem);
            if (removeDirectory) Directory.Delete(events, recursive: true); else File.Delete(path);
            Assert.Null(collector.PollView(Now.AddSeconds(2)).Problem);
            Assert.Null(collector.CurrentView.Problem);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RepairedUnreadableEventWithUnknownProtocolBecomesProviderScopedFailure()
    {
        string root = Fixture();
        try
        {
            var collector = new WorkStatusCollector(new(root));
            string events = Directory.CreateDirectory(Path.Combine(root, "events")).FullName;
            string path = Path.Combine(events, "unreadable.json");
            File.WriteAllText(path, "{");
            Assert.NotNull(collector.PollView(Now).Problem);
            File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(Event(WorkEventKind.Started) with { Protocol = 99 }, WorkStatusStore.JsonOptions));
            WorkStatusViewSnapshot invalid = collector.PollView(Now.AddSeconds(30));
            Assert.Equal("WORK_STATUS_EVENT_INVALID", invalid.Problem);
            Assert.Equal(WorkProvider.CodexDesktop, Assert.Single(invalid.UnavailableProviders!));
            Assert.Equal(WorkProvider.CodexDesktop, Assert.Single(collector.PollView(Now.AddSeconds(32)).UnavailableProviders!));
            Assert.True(File.Exists(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void UnchangedPollReusesPublishedModelWithoutCopyingOrSaving()
    {
        string root = Fixture();
        try
        {
            var store = new WorkStatusStore(root);
            var initial = new WorkStatusModel();
            for (int i = 0; i < 1200; i++)
                initial.Sessions.Add(new(WorkProvider.CodexDesktop, "fixture-" + i, "t", WorkSessionState.Idle, Now, 0));
            store.Save(initial);
            string state = Path.Combine(root, "state.json");
            File.SetLastWriteTimeUtc(state, Now.UtcDateTime);
            DateTime written = File.GetLastWriteTimeUtc(state);
            var collector = new WorkStatusCollector(store);
            WorkStatusView published = collector.CurrentView.Model;
            Assert.Same(published, collector.PollView(Now).Model);

            string spool = Directory.CreateDirectory(Path.Combine(root, "events")).FullName;
            Assert.Same(published, collector.PollView(Now).Model);
            File.WriteAllBytes(Path.Combine(spool, "invalid.json"), JsonSerializer.SerializeToUtf8Bytes(
                Event(WorkEventKind.Started) with { Protocol = 99 }, WorkStatusStore.JsonOptions));
            File.WriteAllText(Path.Combine(spool, "unreadable.json"), "{");
            Assert.Equal("WORK_STATUS_EVENT_UNREADABLE", collector.PollView(Now).Problem);

            long before = GC.GetAllocatedBytesForCurrentThread();
            WorkStatusViewSnapshot poll = collector.PollView(Now.AddSeconds(2));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < new FileInfo(state).Length, $"The poll allocated {allocated} bytes.");
            Assert.Same(published, poll.Model);
            Assert.Equal("WORK_STATUS_EVENT_INVALID", poll.Problem);
            Assert.Null(poll.UnavailableProviders); // The unreadable event cannot be scoped to Codex alone.
            Assert.Equal(written, File.GetLastWriteTimeUtc(state));

            WorkStatusCollector.WriteEvent(root, Event(WorkEventKind.Completed));
            WorkStatusView updated = collector.PollView(Now).Model;
            Assert.NotSame(published, updated);
            Assert.Empty(published.Notices);
            Assert.Single(updated.Notices);
            Assert.Single(store.Load().ProcessedIds);
            Assert.Empty(collector.Current.Model.ProcessedIds);
            WorkStatusModel editableCopy = collector.Current.Model;
            editableCopy.Sessions.Clear();
            Assert.Equal(1201, updated.Sessions.Count);
            Assert.Throws<NotSupportedException>(() => ((IList<WorkSession>)updated.Sessions).Clear());
        }
        finally { Directory.Delete(root, recursive: true); }
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
        WorkStatusView original = collector.CurrentView.Model;
        Exception? error = Record.Exception(() => collector.PollView(Now));
        Assert.Same(original, collector.CurrentView.Model);
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
