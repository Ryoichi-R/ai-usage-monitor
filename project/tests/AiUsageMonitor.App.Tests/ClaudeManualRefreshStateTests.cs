namespace AiUsageMonitor.App.Tests;

public sealed class ClaudeManualRefreshStateTests
{
    [Fact]
    public void MultipleRequestsDuringCodexCoalesceIntoOneFollowUp()
    {
        var state = new ClaudeManualRefreshState();
        state.Enter(RefreshPhase.Codex);

        Assert.True(state.RequestWhileBusy().WasCoalesced);
        Assert.True(state.RequestWhileBusy().WasCoalesced);
        Assert.True(state.CompleteOwner());
        Assert.False(state.CompleteOwner());
    }

    [Fact]
    public async Task RequestDuringClaudeJoinsCurrentTaskWithoutPendingFollowUp()
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new ClaudeManualRefreshState();
        state.Enter(RefreshPhase.Claude, completion.Task);

        ManualRefreshBusyDecision decision = state.RequestWhileBusy();
        Assert.Same(completion.Task, decision.JoinTask);
        Assert.False(decision.WasCoalesced);

        completion.SetResult();
        await decision.JoinTask!;
        Assert.False(state.CompleteOwner());
    }

    [Fact]
    public void RequestDuringDrainDoesNotCreateRecursiveFollowUp()
    {
        var state = new ClaudeManualRefreshState();
        state.BeginDrain();
        state.Enter(RefreshPhase.Codex);

        ManualRefreshBusyDecision decision = state.RequestWhileBusy();

        Assert.Null(decision.JoinTask);
        Assert.False(decision.WasCoalesced);
        Assert.False(state.CompleteOwner());
        state.EndDrain();
    }
}
