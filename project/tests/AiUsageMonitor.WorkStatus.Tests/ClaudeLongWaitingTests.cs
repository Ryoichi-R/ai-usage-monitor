using System.Text.Json;
using System.Text.Json.Nodes;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus.Tests;

public sealed class ClaudeLongWaitingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 7, 0, 0, TimeSpan.Zero);

    private static WorkStatusEvent Hook(string name, int seconds, string tool = "AskUserQuestion", string turn = "turn")
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = name,
            session_id = "synthetic-long-question",
            turn_id = turn,
            tool_name = tool,
            tool_input = "PRIVATE_QUESTION",
            tool_response = "PRIVATE_ANSWER",
        }));
        return HookNormalizer.Normalize(input.RootElement, WorkProvider.ClaudeCode, true, Now.AddSeconds(seconds))!;
    }

    private static WorkStatusModel Waiting()
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("UserPromptSubmit", 0));
        model.Apply(Hook("PreToolUse", 1));
        return model;
    }

    [Fact]
    public void ExplicitQuestionRemainsWaitingAfterTwelveHours()
    {
        var model = Waiting();
        var summary = model.Summarize(WorkProvider.ClaudeCode, Now.AddHours(12), true);
        Assert.Equal(1, summary.Waiting);
        Assert.Equal(0, summary.Unknown);
        Assert.Equal(0, summary.Running);
        Assert.Empty(summary.Notices);
        Assert.Equal(0, model.Summarize(WorkProvider.ClaudeCode, Now.AddHours(12), false).Waiting);
    }

    [Fact]
    public void PendingQuestionSurvivesPersistenceAndUnrelatedToolActivity()
    {
        var model = Waiting();
        model.Apply(Hook("PreToolUse", 600, "Bash"));
        model.Apply(Hook("PostToolUse", 601, "Bash"));
        model.Apply(Hook("PermissionRequest", 602));
        model = WorkStatusStore.Copy(model);
        Assert.Equal(1, model.Summarize(WorkProvider.ClaudeCode, Now.AddHours(12), true).Waiting);
        string saved = JsonSerializer.Serialize(model, WorkStatusStore.JsonOptions);
        Assert.DoesNotContain("PRIVATE_", saved);
    }

    [Theory]
    [InlineData("PostToolUse")]
    [InlineData("PostToolUseFailure")]
    public void ExactQuestionOutcomeReleasesTheLongWait(string outcome)
    {
        var model = Waiting();
        model.Apply(Hook(outcome, 720));
        var summary = model.Summarize(WorkProvider.ClaudeCode, Now.AddMinutes(13), true);
        Assert.Equal(0, summary.Waiting);
        Assert.Equal(1, summary.Running);
        Assert.Equal(0, summary.Candidates);
    }

    [Theory]
    [InlineData("Stop")]
    [InlineData("StopFailure")]
    [InlineData("SessionEnd")]
    [InlineData("UserPromptSubmit")]
    public void ExplicitTerminalOrNewPromptClosesThePendingQuestion(string hook)
    {
        var model = Waiting();
        model.Apply(Hook(hook, 720));
        Assert.Equal(0, model.Summarize(WorkProvider.ClaudeCode, Now.AddMinutes(13), true).Waiting);
    }

    [Fact]
    public void AnOldTurnOutcomeCannotResolveANewerQuestion()
    {
        var model = Waiting();
        model.Apply(Hook("UserPromptSubmit", 10, turn: "new-turn"));
        model.Apply(Hook("PreToolUse", 11, turn: "new-turn"));
        model.Apply(Hook("PostToolUse", 720));
        Assert.Equal(1, model.Summarize(WorkProvider.ClaudeCode, Now.AddHours(12), true).Waiting);
    }

    [Theory]
    [InlineData("PermissionRequest")]
    [InlineData("UserPromptSubmit")]
    public void OtherWaitsAndRunningKeepTheExistingFreshnessLimit(string hook)
    {
        var model = new WorkStatusModel();
        model.Apply(Hook(hook, 0));
        var summary = model.Summarize(WorkProvider.ClaudeCode, Now.AddMinutes(11), true);
        Assert.Equal(0, summary.Waiting);
        Assert.Equal(0, summary.Running);
        Assert.Equal(1, summary.Unknown);
    }

    [Fact]
    public void OlderSnapshotWithoutOptionalMarkerStillLoadsWithOriginalExpiry()
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(Waiting(), WorkStatusStore.JsonOptions))!;
        node["Sessions"]![0]!.AsObject().Remove("InputPending");
        var old = node.Deserialize<WorkStatusModel>(WorkStatusStore.JsonOptions)!;
        var copied = WorkStatusStore.Copy(old);
        Assert.Equal(0, copied.Summarize(WorkProvider.ClaudeCode, Now.AddMinutes(11), true).Waiting);
        Assert.Equal(1, copied.Summarize(WorkProvider.ClaudeCode, Now.AddMinutes(11), true).Unknown);
    }
    [Theory]
    [InlineData(WorkProvider.CodexDesktop, WorkEventKind.Waiting, true)]
    [InlineData(WorkProvider.ClaudeCode, WorkEventKind.Activity, true)]
    [InlineData(WorkProvider.ClaudeCode, WorkEventKind.Waiting, false)]
    public void InputMarkerCannotBeUsedOnAnUnrelatedProviderOrEvent(WorkProvider provider, WorkEventKind kind, bool pending)
    {
        WorkStatusEvent value = Hook("PreToolUse", 0) with { Provider = provider, Kind = kind, InputPending = pending };
        Assert.False(WorkStatusModel.Valid(value));
        Assert.False(new WorkStatusModel().Apply(value));
    }

    [Fact]
    public void ExplicitFalseOutcomeSurvivesEventSerializationAndClearsTheMarker()
    {
        WorkStatusEvent outcome = Hook("PostToolUse", 720);
        string json = JsonSerializer.Serialize(outcome, WorkStatusStore.JsonOptions);
        Assert.Contains("\"InputPending\": false", json);
        outcome = JsonSerializer.Deserialize<WorkStatusEvent>(json, WorkStatusStore.JsonOptions)!;
        var model = Waiting();
        model.Apply(outcome);
        Assert.False(Assert.Single(WorkStatusStore.Copy(model).Sessions).InputPending);
    }

    [Fact]
    public void StoreRejectsAMarkerAttachedToAnInvalidSessionState()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory;
        var store = new WorkStatusStore(Path.Combine(parent, "long-wait-validation", Guid.NewGuid().ToString("N")));
        var model = Waiting();
        model.Sessions[0] = model.Sessions[0] with { State = WorkSessionState.Running };
        Assert.Throws<IOException>(() => store.Save(model));
        Assert.False(Directory.Exists(store.Root));
    }
}