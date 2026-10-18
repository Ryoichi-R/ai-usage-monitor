using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Mac;

namespace AiUsageMonitor.Claude.Mac;

[SupportedOSPlatform("macos")]
/// <param name="diagnostic">理由コードだけの診断（例: VTが拒否した制御の種類）。画面の文字や利用値を渡さない。</param>
public sealed class MacClaudeScreenSessionFactory(
    IManagedProcessLauncher launcher, ClaudeLaunchPolicy policy, ClaudeActiveQuarantine? quarantine = null, Action<string>? diagnostic = null) : IClaudeScreenSessionFactory
{
    public async Task<ScreenSessionResult<IClaudeScreenSession>> StartAsync(string executablePath, string workspacePath, CancellationToken cancellationToken)
    {
        if (!policy.AllowsLaunch()) return ScreenSessionResult<IClaudeScreenSession>.Fail(ClaudeScreenFailureCode.UnsupportedPlatform);
        try
        {
            diagnostic?.Invoke("claude-launch-requested");
            var session = await launcher.StartAsync(policy.CreateStartInfo(executablePath, workspacePath), cancellationToken).ConfigureAwait(false);
            diagnostic?.Invoke("claude-session-started");
            return ScreenSessionResult<IClaudeScreenSession>.Ok(new MacClaudeScreenSession(session, quarantine, diagnostic));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (MacProcessStartException error)
        {
            diagnostic?.Invoke(error.CleanupConfirmed ? "claude-cleanup-clean" : "claude-cleanup-failed");
            diagnostic?.Invoke("claude-identity-mismatch");
            return ScreenSessionResult<IClaudeScreenSession>.Fail(ClaudeScreenFailureCode.ProcessStartFailed);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { diagnostic?.Invoke("claude-cleanup-failed"); return ScreenSessionResult<IClaudeScreenSession>.Fail(ClaudeScreenFailureCode.ProcessStartFailed); }
    }
}

[SupportedOSPlatform("macos")]
internal sealed class MacClaudeScreenSession : IClaudeScreenSession
{
    private readonly IManagedProcessSession _session;
    private readonly ClaudeActiveQuarantine? _quarantine;
    private readonly Action<string>? _diagnostic;
    private bool _rejectionReported;
    private readonly VtScreen _screen = new();
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private long _lastOutput = Stopwatch.GetTimestamp();
    private bool _closed;
    private bool _readyObserved;
    private int _disposed;
    internal MacClaudeScreenSession(IManagedProcessSession session, ClaudeActiveQuarantine? quarantine = null, Action<string>? diagnostic = null)
    { _session = session; _quarantine = quarantine; _diagnostic = diagnostic; _reader = ReadLoopAsync(); }

    public ClaudeScreenFailureCode? CompletionFailure { get; private set; }

    internal static ClaudeScreenFailureCode? ToFailure(ManagedProcessOutcome outcome) => outcome switch
    {
        ManagedProcessOutcome.DescendantEscaped => ClaudeScreenFailureCode.DescendantEscaped,
        ManagedProcessOutcome.SupervisionFailed => ClaudeScreenFailureCode.ProcessCleanupFailed,
        ManagedProcessOutcome.Clean => null,
        _ => ClaudeScreenFailureCode.ProcessCleanupFailed,
    };

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
                if (!_screen.Valid)
                {
                    if (!_rejectionReported)
                    {
                        _rejectionReported = true;
                        _diagnostic?.Invoke("claude-vt-rejected:" + (_screen.RejectedCategory ?? "unknown"));
                    }
                    return ScreenSessionResult<ScreenSnapshot>.Fail(ClaudeScreenFailureCode.ScreenReadFailed);
                }
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
            _readyObserved = true;
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
        // Returning to Ready is diagnostic only: a valid usage snapshot remains valid.
        var returned = await ReadScreenAsync(TimeSpan.FromMilliseconds(Math.Min(1000, timeout.TotalMilliseconds)), cancellationToken).ConfigureAwait(false);
        if (returned.Success && returned.Value is { } snapshot && ClaudeCliScreenStateMachine.Classify(snapshot.Lines) == ClaudeCliScreenSignature.Ready)
            _diagnostic?.Invoke("claude-escape-ready");
        return ScreenSessionResult.Ok();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        try
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            CompletionFailure = ToFailure(_session.Outcome);
            _diagnostic?.Invoke(CompletionFailure is null ? "claude-cleanup-clean" : "claude-cleanup-failed");
            lock (_sync)
            {
                if (!_readyObserved && _screen.Valid && _screen.Complete)
                {
                    string text = string.Join("\n", _screen.Snapshot().Lines);
                    if (_session.ExitCode is > 0 && (text.Contains("unknown option", StringComparison.OrdinalIgnoreCase) || text.Contains("unknown argument", StringComparison.OrdinalIgnoreCase)))
                        _diagnostic?.Invoke("claude-unknown-option");
                }
            }
            if (CompletionFailure == ClaudeScreenFailureCode.DescendantEscaped) _quarantine?.Record();
        }
        finally { await _reader.ConfigureAwait(false); _lifetime.Dispose(); }
    }
}
