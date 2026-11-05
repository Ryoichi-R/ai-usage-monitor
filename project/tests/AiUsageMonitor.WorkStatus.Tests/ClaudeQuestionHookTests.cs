using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus.Tests;

public sealed class ClaudeQuestionHookTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 4, 0, 0, TimeSpan.Zero);

    private static WorkStatusEvent? Hook(string name, int tick, string tool = "AskUserQuestion",
        WorkProvider provider = WorkProvider.ClaudeCode, string agent = "")
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = name,
            session_id = "synthetic-claude-question-session",
            turn_id = "synthetic-turn",
            tool_name = tool,
            agent_id = agent,
            tool_input = "PRIVATE_INPUT_MUST_NOT_BE_PARSED",
            tool_response = "PRIVATE_ANSWER",
            error = "PRIVATE_ERROR",
        }));
        return HookNormalizer.Normalize(input.RootElement, provider, true, Now.AddSeconds(tick));
    }

    [Fact]
    public void QuestionStartsWaitingWithoutReadingQuestionOrAnswerContent()
    {
        WorkStatusEvent value = Assert.IsType<WorkStatusEvent>(Hook("PreToolUse", 1));
        Assert.Equal(WorkEventKind.Waiting, value.Kind);
        Assert.Null(value.QuestionIds);
        string persisted = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("PRIVATE_", persisted);
        Assert.DoesNotContain("AskUserQuestion", persisted);
        Assert.DoesNotContain("synthetic-claude", persisted);
    }

    [Theory]
    [InlineData("PostToolUse")]
    [InlineData("PostToolUseFailure")]
    public void ToolOutcomeReleasesWaitingWithoutDeclaringTheTurnComplete(string outcome)
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("UserPromptSubmit", 0)!);
        model.Apply(Hook("PreToolUse", 1)!);
        Assert.Equal(1, model.Summarize(WorkProvider.ClaudeCode, Now.AddSeconds(2), true).Waiting);
        WorkStatusEvent resolved = Assert.IsType<WorkStatusEvent>(Hook(outcome, 3));
        Assert.Equal(WorkEventKind.Activity, resolved.Kind);
        model.Apply(resolved);
        WorkStatusSummary summary = model.Summarize(WorkProvider.ClaudeCode, Now.AddSeconds(4), true);
        Assert.Equal(0, summary.Waiting);
        Assert.Equal(1, summary.Running);
        Assert.Empty(summary.Notices);
        model.Apply(Hook("Stop", 5)!);
        Assert.Equal(1, model.Summarize(WorkProvider.ClaudeCode, Now.AddSeconds(6), true).Candidates);
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("mcp__example__AskUserQuestion")]
    [InlineData("askuserquestion")]
    public void OnlyTheExactMainQuestionToolCreatesWaiting(string tool)
    {
        Assert.Equal(WorkEventKind.Activity, Hook("PreToolUse", 0, tool)!.Kind);
        Assert.Null(Hook("PostToolUseFailure", 1, tool));
    }

    [Fact]
    public void OtherProviderAndSubagentsKeepTheirExistingBehavior()
    {
        Assert.Equal(WorkEventKind.Activity, Hook("PreToolUse", 0, provider: WorkProvider.CodexDesktop)!.Kind);
        Assert.Null(Hook("PostToolUseFailure", 1, provider: WorkProvider.CodexDesktop));
        Assert.Null(Hook("PreToolUse", 2, agent: "synthetic-subagent"));
    }

    [Fact]
    public void FailureHookExampleIsLimitedToTheQuestionToolAndKeepsContinuePolicy()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string example = HookExamples.Create(WorkProvider.ClaudeCode,
            Path.Combine(root, "bridge.exe"), Path.Combine(root, "state"), true, new Version(2, 1, 310));
        using var json = JsonDocument.Parse(example);
        JsonElement group = json.RootElement.GetProperty("hooks").GetProperty("PostToolUseFailure")[0];
        Assert.Equal("AskUserQuestion", group.GetProperty("matcher").GetString());
        Assert.Equal("continue", group.GetProperty("hooks")[0].GetProperty("onFailure").GetString());
    }
}
