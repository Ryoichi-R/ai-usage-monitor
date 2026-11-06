using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.WorkStatus;

namespace AiUsageMonitor.WorkStatus.Tests;

public sealed class WorkQuestionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    private static WorkStatusEvent? Hook(string hook, int tick, string tool = "request_user_input_async",
        string call = "call-test", int count = 1, string prompt = "", bool accepted = true, string turn = "t")
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            session_id = "s",
            turn_id = turn,
            hook_event_name = hook,
            tool_name = tool,
            tool_use_id = call,
            tool_input = new { questions = Enumerable.Range(0, count).Select(_ => new { title = "PRIVATE_QUESTION" }) },
            tool_response = new { accepted, answer = "PRIVATE_ANSWER" },
            prompt,
        }));
        return HookNormalizer.Normalize(input.RootElement, WorkProvider.CodexDesktop, true, Now.AddSeconds(tick));
    }

    private static string Reply(string call = "call-test", int index = 0) =>
        "<send_user_message_question_reply>\n" + JsonSerializer.Serialize(new[] {
            new { questionItemId = JsonSerializer.Serialize(new object[] { "request_user_input_async", call, index }),
                question = "PRIVATE_QUESTION", answer = "PRIVATE_ANSWER" } }) + "\n</send_user_message_question_reply>";

    [Theory]
    [InlineData("request_user_input_async")]
    [InlineData("functions.request_user_input_async")]
    public void AsynchronousAcceptanceDoesNotResolveTheQuestion(string tool)
    {
        var opened = Hook("PreToolUse", 0, tool)!;
        Assert.Equal(WorkEventKind.QuestionOpened, opened.Kind);
        Assert.Single(opened.QuestionIds!);
        Assert.Null(Hook("PostToolUse", 1, tool));
        string serialized = JsonSerializer.Serialize(opened);
        Assert.DoesNotContain("PRIVATE_", serialized);
        Assert.DoesNotContain("call-test", serialized);
        Assert.DoesNotContain("request_user_input", serialized);
    }

    [Fact]
    public void AsyncQuestionSurvivesToolsAndStopUntilMatchingAnswer()
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("UserPromptSubmit", 0)!);
        model.Apply(Hook("PreToolUse", 1)!);
        model.Apply(Hook("PreToolUse", 2, tool: "exec_command")!);
        model.Apply(Hook("Stop", 4)!);
        Assert.Equal(1, model.Summarize(WorkProvider.CodexDesktop, Now.AddHours(1), true).Waiting);
        Assert.Equal(0, model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(4), true).Running);
        model.Apply(Hook("UserPromptSubmit", 5, prompt: Reply("different"), turn: "newer")!);
        Assert.Single(model.PendingQuestions);
        model.Apply(Hook("UserPromptSubmit", 6, prompt: Reply(), turn: "answered")!);
        Assert.Empty(model.PendingQuestions);
        Assert.Equal(1, model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(6), true).Running);
    }

    [Fact]
    public void OrdinaryMessageClearsEarlierCardsOnlyInTheSameChat()
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("PreToolUse", 0, count: 2)!);
        model.Apply(Hook("Stop", 1)!);
        model.Apply(Hook("PreToolUse", 2)! with { SessionId = "other" });
        WorkStatusEvent message = Hook("UserPromptSubmit", 3, prompt: "PRIVATE_ORDINARY_ANSWER", turn: "new")!;
        Assert.True(message.OrdinaryPrompt);
        Assert.Null(message.QuestionIds);
        Assert.DoesNotContain("PRIVATE_", JsonSerializer.Serialize(message));
        model.Apply(message);
        Assert.Equal("other", Assert.Single(model.PendingQuestions).SessionId);
        Assert.Equal(1, model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(3), true).Running);
        Assert.Equal(1, model.Summarize(WorkProvider.CodexDesktop, Now.AddSeconds(3), true).Waiting);
        model.Apply(Hook("PreToolUse", 4)!);
        Assert.Equal("other", Assert.Single(model.PendingQuestions).SessionId);
    }

    [Fact]
    public void DelayedPromptKeepsNewerCardAndSessionAndRejectsLateOlderCardsAfterRestart()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory;
        string root = Path.Combine(parent, "question-tests", Guid.NewGuid().ToString("N"));
        var store = new WorkStatusStore(root);
        WorkStatusEvent oldCard = Hook("PreToolUse", 0)!;
        // A question saved by an older release has no OpenedAt.
        var legacy = new WorkStatusModel();
        legacy.Apply(oldCard);
        legacy.PendingQuestions[0] = legacy.PendingQuestions[0] with { OpenedAt = null };
        store.Save(legacy);
        var collector = new WorkStatusCollector(store);
        WorkStatusEvent newer = Hook("PreToolUse", 3, call: "newer", turn: "new")!;
        WorkStatusCollector.WriteEvent(root, Hook("PreModelRequest", 2, turn: "new")!);
        WorkStatusCollector.WriteEvent(root, newer);
        collector.Poll(Now.AddSeconds(3));
        WorkStatusCollector.WriteEvent(root, Hook("UserPromptSubmit", 1, prompt: "continue here", turn: "new")!);
        WorkStatusModel model = collector.Poll(Now.AddSeconds(4)).Model;
        Assert.Equal(Assert.Single(newer.QuestionIds!), Assert.Single(model.PendingQuestions).Id);
        Assert.Equal(Now.AddSeconds(2), Assert.Single(model.Sessions).At);
        WorkStatusEvent late = Hook("PreToolUse", 0, call: "late")!;
        WorkStatusCollector.WriteEvent(root, late);
        model = new WorkStatusCollector(store).Poll(Now.AddSeconds(5)).Model;
        Assert.Equal(Assert.Single(newer.QuestionIds!), Assert.Single(model.PendingQuestions).Id);
    }

    [Fact]
    public void PartialAnswersAndMultipleCardsAreCorrelatedIndependently()
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("PreToolUse", 0, count: 2)!);
        model.Apply(Hook("PreToolUse", 1, call: "second")!);
        model.Apply(Hook("UserPromptSubmit", 2, prompt: Reply())!);
        Assert.Equal(2, model.PendingQuestions.Count);
        model.Apply(Hook("UserPromptSubmit", 3, prompt: Reply(index: 1))!);
        Assert.Single(model.PendingQuestions);
        model.Apply(Hook("UserPromptSubmit", 4, prompt: Reply("second"))!);
        Assert.Empty(model.PendingQuestions);
    }

    [Fact]
    public void ResolvedQuestionCannotReopenOnDuplicateOrLateDelivery()
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("UserPromptSubmit", 1, prompt: Reply())!);
        model.Apply(Hook("PreToolUse", 0)!);
        model.Apply(Hook("PreToolUse", 2)!);
        Assert.Empty(model.PendingQuestions);
    }

    [Fact]
    public void BlockingQuestionClosesOnToolResultAndRejectedAsyncCloses()
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("PreToolUse", 0, tool: "request_user_input")!);
        model.Apply(Hook("PostToolUse", 1, tool: "request_user_input")!);
        Assert.Empty(model.PendingQuestions);
        model.Apply(Hook("PreToolUse", 2, call: "rejected")!);
        model.Apply(Hook("PostToolUse", 3, call: "rejected", accepted: false)!);
        Assert.Empty(model.PendingQuestions);
    }

    [Theory]
    [InlineData("<send_user_message_question_reply>[]</send_user_message_question_reply>")]
    [InlineData("<send_user_message_question_reply>{broken}</send_user_message_question_reply>")]
    [InlineData("  ")]
    [InlineData("prefix <send_user_message_question_reply>{broken}</send_user_message_question_reply>")]
    public void UnrecognizedReplyDoesNotGuessAnAnswer(string prompt)
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("PreToolUse", 0)!);
        WorkStatusEvent value = Hook("UserPromptSubmit", 1, prompt: prompt)!;
        Assert.False(value.OrdinaryPrompt);
        model.Apply(value);
        Assert.Single(model.PendingQuestions);
    }

    [Fact]
    public void PendingQuestionSurvivesSaveRestartAndAcknowledgementWithoutPrivateData()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? AppContext.BaseDirectory;
        string root = Path.Combine(parent, "question-tests", Guid.NewGuid().ToString("N"));
        var collector = new WorkStatusCollector(new(root));
        WorkStatusCollector.WriteEvent(root, Hook("PreToolUse", 0)!);
        collector.Poll(Now);
        collector.Acknowledge([]);
        var restarted = new WorkStatusCollector(new(root));
        Assert.Equal(1, restarted.Current.Model.Summarize(WorkProvider.CodexDesktop, Now.AddHours(1), true).Waiting);
        string state = File.ReadAllText(Path.Combine(root, "state.json"));
        Assert.DoesNotContain("PRIVATE_", state);
        Assert.DoesNotContain("call-test", state);
        Assert.DoesNotContain("request_user_input", state);
        WorkStatusCollector.WriteEvent(root, Hook("UserPromptSubmit", 1, prompt: Reply())!);
        Assert.Empty(restarted.Poll(Now.AddSeconds(1)).Model.PendingQuestions);
    }

    [Fact]
    public void SessionEndClearsOnlyItsPendingQuestions()
    {
        var model = new WorkStatusModel();
        model.Apply(Hook("PreToolUse", 0)!);
        model.Apply(Hook("PreToolUse", 1)! with { SessionId = "other" });
        model.Apply(Hook("SessionEnd", 2)!);
        Assert.Equal("other", Assert.Single(model.PendingQuestions).SessionId);
    }

    [Fact]
    public void OldSchemaLoadsWithEmptyQuestionCollectionsAndCorruptQuestionsAreRejected()
    {
        var model = JsonSerializer.Deserialize<WorkStatusModel>("""
            {"Version":1,"Sessions":[],"Notices":[],"ProcessedIds":[],"AcknowledgedIds":[],"Imports":[]}
            """)!;
        Assert.Empty(model.PendingQuestions);
        Assert.Empty(model.AnsweredQuestionIds);
        Assert.False(WorkStatusModel.Valid(Hook("PreToolUse", 0)! with { QuestionIds = ["invalid"] }));
        Assert.False(WorkStatusModel.Valid(Hook("PreToolUse", 0)! with { Provider = WorkProvider.ClaudeCode }));
    }
}
