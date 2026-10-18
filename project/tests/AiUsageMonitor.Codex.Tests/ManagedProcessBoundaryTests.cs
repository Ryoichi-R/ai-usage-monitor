using System.Diagnostics;
using AiUsageMonitor.Codex.Process;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Codex.Tests;

public sealed class ManagedProcessBoundaryTests
{
    [Fact]
    public async Task StartupTimeoutIncludesSupervisorPreparation()
    {
        var launcher = new DelayedLauncher();
        await using var server = new CodexAppServerProcess();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => server.StartAsync(
            "/fake/codex", null, TimeSpan.FromMilliseconds(30), null, launcher, CancellationToken.None));
        Assert.True(launcher.Started);
        Assert.True(launcher.Cancelled);
    }

    [Fact]
    public async Task FailedInitializeDisposesManagedSessionBeforeReturning()
    {
        var launcher = new EmptyLauncher();
        await using var server = new CodexAppServerProcess();
        await Assert.ThrowsAnyAsync<IOException>(() => server.StartAsync(
            "/fake/codex", null, TimeSpan.FromSeconds(1), null, launcher, CancellationToken.None));
        Assert.True(launcher.Session.Disposed);
        Assert.Equal("app-server", Assert.Single(launcher.Arguments!));
    }

    private sealed class DelayedLauncher : IManagedProcessLauncher
    {
        public bool Started { get; private set; }
        public bool Cancelled { get; private set; }
        public async Task<IManagedProcessSession> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            Started = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException();
        }
    }
    private sealed class EmptyLauncher : IManagedProcessLauncher
    {
        public EmptySession Session { get; } = new();
        public string[]? Arguments { get; private set; }
        public Task<IManagedProcessSession> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
        {
            Arguments = startInfo.ArgumentList.ToArray();
            return Task.FromResult<IManagedProcessSession>(Session);
        }
    }
    private sealed class EmptySession : IManagedProcessSession
    {
        public StreamReader StandardOutput { get; } = new(new MemoryStream());
        public StreamReader StandardError { get; } = new(new MemoryStream());
        public StreamWriter StandardInput { get; } = new(new MemoryStream());
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            StandardOutput.Dispose(); StandardError.Dispose(); StandardInput.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
