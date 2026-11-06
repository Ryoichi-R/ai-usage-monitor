using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus.Tests;

public sealed class CodexModelActivityHookTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 6, 0, 0, TimeSpan.Zero);

    private static WorkStatusEvent? Hook(string name, int tick, WorkProvider provider = WorkProvider.CodexDesktop,
        bool verified = true, string agent = "")
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            hook_event_name = name,
            session_id = "synthetic-model-session",
            turn_id = "synthetic-model-turn",
            agent_id = agent,
            model = "synthetic-model",
            request = "PRIVATE_MODEL_INPUT_MUST_NOT_BE_PARSED",
            prompt = "PRIVATE_PROMPT_MUST_NOT_BE_READ",
            error = "PRIVATE_ERROR",
        }));
        return HookNormalizer.Normalize(input.RootElement, provider, verified, Now.AddSeconds(tick));
    }

    [Fact]
    public void RequestStartUsesOnlyVerifiedMainSessionMetadata()
    {
        WorkStatusEvent value = Assert.IsType<WorkStatusEvent>(Hook("PreModelRequest", 0));
        Assert.Equal(WorkEventKind.Started, value.Kind);
        Assert.Null(value.QuestionIds);
        Assert.False(value.OrdinaryPrompt);
        Assert.Equal(HookNormalizer.Digest("synthetic-model-session"), value.SessionId);
        Assert.Equal(HookNormalizer.Digest("synthetic-model-turn"), value.TurnId);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(value));
    }

    [Fact]
    public void ConnectedSessionBecomesRunningWithoutPromptOrToolEvents()
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("SessionStart", 0)!);
        Assert.Equal(0, model.Summarize(WorkProvider.CodexDesktop, Now, true).Running);
        model.Apply(Hook("PreModelRequest", 1)!);
        Assert.Equal(1, model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(2), true).Running);
        model.Apply(Hook("Stop", 3)!);
        Assert.Equal(0, model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(4), true).Running);
        Assert.Equal(1, model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(4), true).Candidates);
    }

    [Fact]
    public void RequestStartDoesNotClearAnUnansweredCodexCard()
    {
        var model = new WorkStatusModel();
        WorkStatusEvent started = Hook("UserPromptSubmit", 0)!;
        model.Apply(started);
        model.Apply(started with
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = WorkEventKind.QuestionOpened,
            At = Now.AddSeconds(1),
            Order = Now.AddSeconds(1).UtcTicks,
            QuestionIds = [new string('A', 24)],
            OrdinaryPrompt = false,
        });
        model.Apply(Hook("PreModelRequest", 2)!);
        WorkStatusSummary summary = model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(3), true);
        Assert.Equal(0, summary.Running);
        Assert.Equal(1, summary.Waiting);
        Assert.Single(model.PendingQuestions);
    }

    [Fact]
    public void OtherProvidersSubagentsAndUnverifiedSourcesRemainRejected()
    {
        Assert.Null(Hook("PreModelRequest", 0, WorkProvider.ClaudeCode));
        Assert.Null(Hook("PreModelRequest", 0, verified: false));
        Assert.Null(Hook("PreModelRequest", 0, agent: "synthetic-subagent"));
    }

    [Fact]
    public void RequestOutcomeIsNotATurnCompletionSignal()
    {
        Assert.Null(Hook("PostModelRequest", 0));
    }

    [Fact]
    public void ConnectionExampleRegistersRequestStartOnlyForCodex()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        string bridge = Path.Combine(root, "bridge"), state = Path.Combine(root, "state");
        using var codex = JsonDocument.Parse(HookExamples.Create(WorkProvider.CodexDesktop,
            bridge, state, OperatingSystem.IsWindows()));
        using var claude = JsonDocument.Parse(HookExamples.Create(WorkProvider.ClaudeCode,
            bridge, state, OperatingSystem.IsWindows(), new Version(2, 1, 310)));
        Assert.True(codex.RootElement.GetProperty("hooks").TryGetProperty("PreModelRequest", out _));
        Assert.False(codex.RootElement.GetProperty("hooks").TryGetProperty("PostModelRequest", out _));
        Assert.False(claude.RootElement.GetProperty("hooks").TryGetProperty("PreModelRequest", out _));
    }
}
