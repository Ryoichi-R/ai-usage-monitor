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

    [Fact]
    public async Task SaveRetriesWhileAHandleWithoutDeleteSharingBrieflyHoldsTheSnapshot()
    {
        // ReplaceFile fails with ERROR_UNABLE_TO_REMOVE_REPLACED only on Windows.
        if (!OperatingSystem.IsWindows()) return;
        string root = CreateRoot("work-replace-retry");
        var writer = new WorkStatusStore(root);
        var model = new WorkStatusModel();
        writer.Save(model);
        // Mimics an on-access scanner that opened the previous snapshot without FILE_SHARE_DELETE.
        var holder = new FileStream(Path.Combine(root, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Task release = Task.Run(async () =>
        {
            await Task.Delay(150);
            await holder.DisposeAsync();
        });
        try
        {
            model.AcknowledgedIds.Add("ack");
            writer.Save(model);
        }
        finally { await release; }
        Assert.Equal(["ack"], writer.Load().AcknowledgedIds);
        Assert.Equal(["state.json"], Directory.GetFiles(root).Select(Path.GetFileName));
    }

    [Fact]
    public void SaveStillFailsClosedWhenTheSnapshotStaysLocked()
    {
        if (!OperatingSystem.IsWindows()) return;
        string root = CreateRoot("work-replace-locked");
        var writer = new WorkStatusStore(root);
        var model = new WorkStatusModel();
        writer.Save(model);
        using (new FileStream(Path.Combine(root, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            model.AcknowledgedIds.Add("ack");
            Assert.ThrowsAny<IOException>(() => writer.Save(model));
        }
        Assert.Empty(writer.Load().AcknowledgedIds);
        Assert.Equal(["state.json"], Directory.GetFiles(root).Select(Path.GetFileName));
    }

    private static string CreateRoot(string name)
    {
        string parent = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT") ?? Path.GetTempPath();
        return Path.Combine(parent, name, Guid.NewGuid().ToString("N"));
    }
}
