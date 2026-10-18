using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Tests;

public sealed class ManagedProcessContractTests
{
    [Fact]
    public async Task SessionsWithoutCleanupReportExposeUnknownOutcome()
    {
        // Job Object等、後始末の結果を報告しない実装は未確定のままとし、Cleanと誤認させない。
        IManagedProcessSession session = new SilentSession();
        await session.DisposeAsync();
        Assert.Equal(ManagedProcessOutcome.Unknown, session.Outcome);
    }

    private sealed class SilentSession : IManagedProcessSession
    {
        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public StreamWriter StandardInput { get; } = new(new MemoryStream());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
