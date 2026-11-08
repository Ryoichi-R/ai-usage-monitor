using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.WorkStatus;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class CodexWaitPresentationTests
{
    [Fact]
    public void ParallelQuestionsAndPermissionsHaveSeparateLabelsAndRetainTotalWaiting()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "start", WorkProvider.CodexDesktop, "question", "t", WorkEventKind.Started, now, true));
        model.Apply(new(1, "question", WorkProvider.CodexDesktop, "question", "t", WorkEventKind.QuestionOpened,
            now.AddSeconds(1), true, QuestionIds: ["0123456789ABCDEF01234567"]));
        model.Apply(new(1, "permission", WorkProvider.CodexDesktop, "permission", "t", WorkEventKind.Waiting, now, true));
        // A permission event in the question session must not count the same session twice.
        model.Apply(new(1, "question-permission", WorkProvider.CodexDesktop, "question", "t", WorkEventKind.Waiting,
            now.AddSeconds(2), true));
        model = WorkStatusStore.Copy(model);
        var summary = model.Summarize(WorkProvider.CodexDesktop, now.AddSeconds(3), true);
        Assert.Equal(2, summary.Waiting);
        Assert.Equal(1, summary.QuestionWaiting);
        Assert.Equal(1, summary.PermissionWaiting);
        var view = new WorkStatusViewModel();
        view.Update(model, true, false, null, now.AddSeconds(3));
        Assert.Equal("質問待ち / 権限確認中", view.Rows[0].Status);
        Assert.Equal([true, false], view.Rows[0].StatusParts.Select(part => part.IsWaiting));
        Assert.Equal([false, true], view.Rows[0].StatusParts.Select(part => part.IsRunning));
        // Permission waits expire, but unanswered questions do not.
        view.Update(model, true, false, null, now.AddHours(12));
        Assert.Equal("質問待ち", view.Rows[0].Status);
        view.Update(model, true, false, "unavailable", now.AddHours(12));
        Assert.Equal("—", view.Rows[0].Status);
    }

    [Fact]
    public void PermissionCompletionResumesWorkWithoutCreatingAQuestion()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        var view = new WorkStatusViewModel();
        model.Apply(new(1, "permission", WorkProvider.CodexDesktop, "s", "t", WorkEventKind.Waiting, now, true));
        view.Update(model, true, false, null, now);
        Assert.Equal("権限確認中", view.Rows[0].Status);
        Assert.Empty(model.PendingQuestions);
        model.Apply(new(1, "activity", WorkProvider.CodexDesktop, "s", "t", WorkEventKind.Activity, now.AddSeconds(1), true));
        view.Update(model, true, false, null, now.AddSeconds(1));
        Assert.Equal("作業中 1", view.Rows[0].Status);
    }

    [Fact]
    public void AnsweringAQuestionRemovesOnlyItsLabel()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        model.Apply(new(1, "question", WorkProvider.CodexDesktop, "q", "t", WorkEventKind.QuestionOpened,
            now, true, QuestionIds: ["0123456789ABCDEF01234567"]));
        model.Apply(new(1, "permission", WorkProvider.CodexDesktop, "p", "t", WorkEventKind.Waiting, now, true));
        model.Apply(new(1, "answer", WorkProvider.CodexDesktop, "q", "t", WorkEventKind.QuestionAnswered,
            now.AddSeconds(1), true, QuestionIds: ["0123456789ABCDEF01234567"]));
        var view = new WorkStatusViewModel();
        view.Update(model, true, false, null, now.AddSeconds(1));
        Assert.Equal("作業中 1 / 権限確認中", view.Rows[0].Status);
    }
}
