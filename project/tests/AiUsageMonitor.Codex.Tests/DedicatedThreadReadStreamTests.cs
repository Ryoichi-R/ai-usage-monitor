using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using AiUsageMonitor.Codex.Process;

namespace AiUsageMonitor.Codex.Tests;

public sealed class DedicatedThreadReadStreamTests
{
    [Fact]
    public async Task DeliversLinesAndEndsAtEof()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.In);
        using var client = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
        using var reader = new StreamReader(new DedicatedThreadReadStream(server, "test-pipe"), Encoding.UTF8);

        byte[] payload = Encoding.UTF8.GetBytes("first\nsecond\n");
        client.Write(payload);
        client.Flush();
        Assert.Equal("first", await reader.ReadLineAsync());
        Assert.Equal("second", await reader.ReadLineAsync());
        client.Dispose();
        server.DisposeLocalCopyOfClientHandle();
        Assert.Null(await reader.ReadLineAsync());
    }

    [Fact]
    public async Task CancellationStopsWaitingWhileThePipeStaysOpen()
    {
        using var server = new AnonymousPipeServerStream(PipeDirection.In);
        using var client = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
        using var stream = new DedicatedThreadReadStream(server, "test-pipe");
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.ReadAsync(new byte[16], cancel.Token).AsTask());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Cancellation took {watch.Elapsed}.");
    }

    [Fact]
    public async Task PendingReadsOnIdlePipesDoNotOccupyThreadPoolThreads()
    {
        // Anonymous pipes cannot do overlapped I/O on Windows, like process redirection pipes.
        // Far more idle readers than the minimum worker count would starve the pool if each
        // pending ReadAsync held a pool thread.
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        int readers = Math.Max(24, minWorkers * 3);
        var pipes = new List<(AnonymousPipeServerStream Server, AnonymousPipeClientStream Client)>();
        var pending = new List<Task<string?>>();
        try
        {
            for (int index = 0; index < readers; index++)
            {
                var server = new AnonymousPipeServerStream(PipeDirection.In);
                var client = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
                pipes.Add((server, client));
                var reader = new StreamReader(new DedicatedThreadReadStream(server, "test-pipe-" + index), Encoding.UTF8);
                pending.Add(reader.ReadLineAsync(CancellationToken.None).AsTask());
            }

            TimeSpan worst = MeasureWorstThreadPoolDispatch(probes: 20);
            Assert.True(worst < TimeSpan.FromSeconds(1),
                $"A thread-pool work item waited {worst.TotalMilliseconds:F0} ms with {readers} idle readers.");
            Assert.All(pending, task => Assert.False(task.IsCompleted));
        }
        finally
        {
            foreach (var (server, client) in pipes)
            {
                client.Dispose();
                server.DisposeLocalCopyOfClientHandle();
            }
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
            foreach (var (server, _) in pipes) server.Dispose();
        }
    }

    // Queues to the global queue from a non-pool thread, so the probe itself never borrows a pool thread.
    internal static TimeSpan MeasureWorstThreadPoolDispatch(int probes)
    {
        TimeSpan worst = TimeSpan.Zero;
        var probe = new Thread(() =>
        {
            for (int index = 0; index < probes; index++)
            {
                using var ran = new ManualResetEventSlim();
                var watch = Stopwatch.StartNew();
                ThreadPool.UnsafeQueueUserWorkItem<ManualResetEventSlim>(signal => signal.Set(), ran, preferLocal: false);
                ran.Wait();
                if (watch.Elapsed > worst) worst = watch.Elapsed;
                Thread.Sleep(25);
            }
        })
        { IsBackground = true };
        probe.Start();
        probe.Join();
        return worst;
    }
}
