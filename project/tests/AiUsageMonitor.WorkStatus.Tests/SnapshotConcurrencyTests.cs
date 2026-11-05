using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus.Tests;

public sealed class SnapshotConcurrencyTests
{
    [Fact]
    public async Task ReaderDoesNotBlockAtomicSnapshotReplacement()
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? Path.GetTempPath();
        string root = Path.Combine(parent, "work-concurrent", Guid.NewGuid().ToString("N"));
        var writer = new WorkStatusStore(root);
        var model = new WorkStatusModel();
        for (int index = 0; index < 400; index++)
            model.Sessions.Add(new(WorkProvider.CodexDesktop, "session-" + index, "turn",
                WorkSessionState.Running, DateTimeOffset.UtcNow, index));
        writer.Save(model);
        using var cancel = new CancellationTokenSource();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reader = Task.Run(() =>
        {
            var store = new WorkStatusStore(root);
            Assert.Equal(400, store.Load().Sessions.Count);
            ready.SetResult();
            while (!cancel.IsCancellationRequested)
                Assert.Equal(400, store.Load().Sessions.Count);
        });
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            for (int index = 0; index < 40; index++)
            {
                model.AcknowledgedIds.Add("ack-" + index);
                writer.Save(model);
            }
        }
        finally
        {
            cancel.Cancel();
            await reader;
        }
        Assert.Equal(40, writer.Load().AcknowledgedIds.Count);
    }
}
