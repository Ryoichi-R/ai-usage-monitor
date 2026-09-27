using System.Diagnostics;
using System.Text;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Claude.Mac;

public sealed class MacClaudeScreenSessionFactory(IManagedProcessLauncher launcher, ClaudeLaunchPolicy policy) : IClaudeScreenSessionFactory
{
    public async Task<ScreenSessionResult<IClaudeScreenSession>> StartAsync(string executablePath, string workspacePath, CancellationToken cancellationToken)
    {
        if (!policy.AllowsLaunch()) return ScreenSessionResult<IClaudeScreenSession>.Fail(ClaudeScreenFailureCode.UnsupportedPlatform);
        try
        {
            var session = await launcher.StartAsync(policy.CreateStartInfo(executablePath, workspacePath), cancellationToken).ConfigureAwait(false);
            return ScreenSessionResult<IClaudeScreenSession>.Ok(new MacClaudeScreenSession(session));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { return ScreenSessionResult<IClaudeScreenSession>.Fail(ClaudeScreenFailureCode.ProcessStartFailed); }
    }
}

internal sealed class MacClaudeScreenSession : IClaudeScreenSession
{
    private readonly IManagedProcessSession _session;
    private readonly VtScreen _screen = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private long _lastOutput = Stopwatch.GetTimestamp();
    private bool _closed;
    private int _disposed;
    internal MacClaudeScreenSession(IManagedProcessSession session) { _session = session; _reader = ReadLoopAsync(); }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[4096];
        try
        {
            int length;
            while ((length = await _session.StandardOutput.BaseStream.ReadAsync(buffer, _lifetime.Token).ConfigureAwait(false)) > 0)
                lock (_sync) { _screen.Feed(buffer.AsSpan(0, length)); _lastOutput = Stopwatch.GetTimestamp(); }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { lock (_sync) _closed = true; }
    }

    public async Task<ScreenSessionResult<ScreenSnapshot>> ReadScreenAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                if (_closed) return ScreenSessionResult<ScreenSnapshot>.Fail(ClaudeScreenFailureCode.ProcessExited);
                if (!_screen.Valid) return ScreenSessionResult<ScreenSnapshot>.Fail(ClaudeScreenFailureCode.ScreenReadFailed);
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(350) && _screen.Complete && _screen.Revision > 0 && Stopwatch.GetElapsedTime(_lastOutput) >= TimeSpan.FromMilliseconds(350))
                    return ScreenSessionResult<ScreenSnapshot>.Ok(_screen.Snapshot());
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
        return ScreenSessionResult<ScreenSnapshot>.Fail(ClaudeScreenFailureCode.HelperTimeout);
    }

    public async Task<ScreenSessionResult> SendUsageCommandAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var screen = await ReadScreenAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!screen.Success) return ScreenSessionResult.Fail(screen.ReasonCode ?? ClaudeScreenFailureCode.ScreenReadFailed);
        lock (_sync)
        {
            if (_closed || !_screen.Complete || Stopwatch.GetElapsedTime(_lastOutput) < TimeSpan.FromMilliseconds(350) ||
                ClaudeCliScreenStateMachine.Classify(_screen.Snapshot().Lines) != ClaudeCliScreenSignature.Ready)
                return ScreenSessionResult.Fail(ClaudeScreenFailureCode.InvalidOperation);
            // Small pipe write under the same lock as the screen update: no observed transition can race the readiness check.
            _session.StandardInput.Write("/usage\r"); _session.StandardInput.Flush();
        }
        return ScreenSessionResult.Ok();
    }

    public async Task<ScreenSessionResult> SendEscapeAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var screen = await ReadScreenAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (!screen.Success) return ScreenSessionResult.Fail(screen.ReasonCode ?? ClaudeScreenFailureCode.ScreenReadFailed);
        lock (_sync)
        {
            if (_closed || !_screen.Complete || Stopwatch.GetElapsedTime(_lastOutput) < TimeSpan.FromMilliseconds(350) || ClaudeCliScreenStateMachine.Classify(_screen.Snapshot().Lines) != ClaudeCliScreenSignature.UsageScreen)
                return ScreenSessionResult.Fail(ClaudeScreenFailureCode.InvalidOperation);
            _session.StandardInput.Write('\x1b'); _session.StandardInput.Flush();
        }
        return ScreenSessionResult.Ok();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try { await _session.DisposeAsync().ConfigureAwait(false); }
        finally { await _reader.ConfigureAwait(false); _lifetime.Dispose(); }
    }
}
