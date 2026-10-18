using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Mac;

namespace AiUsageMonitor.Claude.Mac;

/// <summary>Inspects every poll, retries per candidate, and commits values only after durable cleanup.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacClaudeAutomaticSource : IClaudeScheduledUsageSource, IDisposable
{
    private static readonly int[] RetryMinutes = [1, 5, 15, 30, 60];
    private readonly IClaudeExecutableLocator _locator;
    private readonly ClaudeCompatibilityStore _store;
    private readonly Func<ClaudeExecutableInfo, Action<string>, Func<string?>, IClaudeUsageSource> _createSource;
    private readonly string? _configuredPath;
    private readonly string _contract;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly TimeSpan _deadline;
    private readonly TimeProvider _time;
    private string? _candidateId;
    private int _failures;
    private DateTimeOffset? _next;
    private string _lastFailure = "CLI_COMPATIBILITY_CHECKING";
    private bool _cleanupConfirmed;
    private bool _vtRejected;
    private bool _unknownOption;
    private bool _identityMismatch;
    private bool _codeConfirmed;
    public event Action<UsageSnapshot>? ProgressChanged;
    public DateTimeOffset? NextAttemptAt { get { lock (_sync) return _next; } }

    public MacClaudeAutomaticSource(string? configuredPath, string helper, string journal, string bridge,
        TimeSpan startupTimeout, IClaudeWorkspaceProvisioner workspace, ClaudeLaunchPolicy policy,
        ClaudeCompatibilityStore store, Action<string>? diagnostic = null)
        : this(configuredPath, new MacClaudeExecutableLocator(policy.Home, helper, policy),
            store, "parser:1|" + typeof(MacClaudeAutomaticSource).Assembly.GetName().Version + "|" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(helper))),
            TimeSpan.FromSeconds(startupTimeout.TotalSeconds * 2 + 30),
            (candidate, report, expected) =>
            {
                var probe = new MacClaudeCapabilityProbe(new MacManagedProcessLauncher(helper, journal, verifyClaude: true, expectedIdentity: expected),
                    policy, workspace, completed: outcome => report(outcome == ManagedProcessOutcome.Clean ? "claude-cleanup-clean" : "claude-cleanup-failed"), cacheIdentity: expected,
                    starting: () => report("claude-launch-requested"), launched: () => report("claude-session-started"));
                return new ClaudeCliActiveSource(configuredPath, bridge, startupTimeout, workspace,
                    new MacClaudeScreenSessionFactory(new MacManagedProcessLauncher(helper, journal, usePty: true, verifyClaude: true, expectedIdentity: expected),
                        policy, diagnostic: code => { diagnostic?.Invoke(code); report(code); }),
                    new CandidateLocator(candidate), probe.ProbeAsync, executionGateHeld: true);
            })
    { }

    public MacClaudeAutomaticSource(string? configuredPath, IClaudeExecutableLocator locator, ClaudeCompatibilityStore store,
        string contract, TimeSpan deadline, Func<ClaudeExecutableInfo, Action<string>, Func<string?>, IClaudeUsageSource> createSource, TimeProvider? time = null)
    { _configuredPath = configuredPath; _locator = locator; _store = store; _contract = contract; _deadline = deadline; _createSource = createSource; _time = time ?? TimeProvider.System; }

    public void Cancel() => _lifetime.Cancel();
    public void Dispose() => _lifetime.Dispose();
    public void ResetRetry() { lock (_sync) { _next = null; _failures = 0; } }
    public void Recheck(bool releaseSafety)
    {
        if (!ClaudeCliActiveSource.ConsoleExecutionGate.Wait(0)) throw new IOException("取得中です。完了後に再確認してください。");
        try { _store.Release(releaseSafety, releaseSafety ? null : _candidateId); ResetRetry(); }
        finally { ClaudeCliActiveSource.ConsoleExecutionGate.Release(); }
    }
    public string DescribeStop()
    {
        try
        {
            var state = _store.Read();
            if (state.Pending is { } pending) return $"前回の確認が未完了です。検出: {pending.StartedAt:O}\n対象: {pending.BinaryId[..Math.Min(12, pending.BinaryId.Length)]}";
            return string.Join("\n", state.Blocks.Where(b => b.ReleasedAt is null).Select(b => $"{b.Reason} / {b.DetectedAt:O} / {b.BinaryId[..Math.Min(12, b.BinaryId.Length)]}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { return "停止記録を確認できません。troubleshootingの互換性状態の項を確認してください。"; }
    }

    public async Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(_deadline);
        await ClaudeCliActiveSource.ConsoleExecutionGate.WaitAsync(deadline.Token).ConfigureAwait(false);
        ClaudeCompatibilityState? state = null;
        bool pending = false;
        string? identity = null;
        try
        {
            // This inspection precedes retry admission, including during a 60-minute backoff.
            ClaudeExecutableInfo candidate = _locator.Resolve(_configuredPath);
            if (candidate.FailureReason is { } failure)
            {
                if (failure is "SIGNATURE_VERIFICATION_FAILED" or "CLI_IDENTITY_INVALID")
                {
                    var unresolved = _store.Read();
                    unresolved.Pending ??= new(Guid.NewGuid(), "unresolved", _time.GetUtcNow());
                    _store.Write(unresolved);
                }
                return Failure(failure, failure == "CLAUDE_NOT_INSTALLED" ? UsageAvailability.NotInstalled : UsageAvailability.Unsupported);
            }
            identity = MacClaudeBinaryIdentity.Parse(candidate.Identity ?? "").Key;
            bool changed;
            lock (_sync)
            {
                changed = _candidateId != identity;
                if (changed) { _candidateId = identity; _next = null; _failures = 0; }
            }
            state = _store.Read();
            if (state.Pending is not null) { ResetRetry(); return Failure("CLI_PREVIOUS_ATTEMPT_INCOMPLETE", UsageAvailability.Unsupported); }
            var block = state.Blocks.LastOrDefault(b => b.BinaryId == identity && b.ReleasedAt is null && (b.Contract.Length == 0 || b.Contract == _contract));
            if (block is not null) { ResetRetry(); return Failure(block.Reason, UsageAvailability.Unsupported); }
            if (NextAttemptAt is { } retry && _time.GetUtcNow() < retry) return Failure(_failures >= 3 ? "CLI_CONFIRMATION_RETRYING" : _lastFailure);
            if (changed || state.LastBinaryId != identity)
                ProgressChanged?.Invoke(UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with { Reason = "CLI_COMPATIBILITY_CHECKING" });
            state.Pending = new(Guid.NewGuid(), identity, DateTimeOffset.UtcNow);
            _store.Write(state);
            pending = true;
            _cleanupConfirmed = true; _vtRejected = false; _unknownOption = false; _identityMismatch = false; _codeConfirmed = false;
            var source = _createSource(candidate, Report, () => candidate.Identity);
            ClaudeUsageObservation result = await source.RefreshAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            // A missing startup protocol cannot attribute the unknown process to the inspected binary.
            // Retain the attempt globally until investigated, including after a candidate update.
            if (!_cleanupConfirmed && !_codeConfirmed) return Failure("CLI_PREVIOUS_ATTEMPT_INCOMPLETE", UsageAvailability.Unsupported);
            if (result.Snapshot.Reason is "CLI_GROUP_ESCAPE_DETECTED" or "PROCESS_CLEANUP_FAILED")
                return Stop(state, identity, result.Snapshot.Reason);
            if (!_cleanupConfirmed) return Stop(state, identity, "PROCESS_CLEANUP_FAILED");
            if (_vtRejected || _unknownOption)
            {
                string reason = _vtRejected ? "CLI_SCREEN_INCOMPATIBLE" : "CLI_ARGUMENT_INCOMPATIBLE";
                state.Blocks.Add(new(identity, _contract, reason, DateTimeOffset.UtcNow));
                Finish(state); return Failure(reason, UsageAvailability.Unsupported);
            }
            ClaudeExecutableInfo current = _locator.Resolve(_configuredPath);
            if (current.FailureReason is not null) return Stop(state, identity, "CLI_IDENTITY_MISMATCH");
            if (current.Identity != candidate.Identity)
            { Finish(state); ResetRetry(); return Failure("CLI_CANDIDATE_CHANGED"); }
            if (_identityMismatch || result.Reason == "CLI_IDENTITY_MISMATCH") return Stop(state, identity, "CLI_IDENTITY_MISMATCH");
            if (result.Availability == UsageAvailability.Available)
            {
                state.LastBinaryId = identity;
                Finish(state); ResetRetry(); return result;
            }
            Finish(state);
            if (result.Availability is not (UsageAvailability.Setup or UsageAvailability.SignedOut)) Backoff(result.Snapshot.Reason ?? "CLI_CONFIRMATION_FAILED");
            return _failures >= 3 ? new(result.Source, result.Snapshot with { Reason = "CLI_CONFIRMATION_RETRYING" }) : result;
        }
        catch (OperationCanceledException)
        {
            if (pending && state is not null && _cleanupConfirmed) Finish(state);
            if (cancellationToken.IsCancellationRequested || _lifetime.IsCancellationRequested) throw;
            Backoff("CLI_CONFIRMATION_TIMEOUT"); return Failure("CLI_CONFIRMATION_TIMEOUT");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            // Never clear a pending record when cleanup or durable finalization was not proven.
            if (pending && state is not null && identity is not null && error.Message == "CLI_IDENTITY_MISMATCH")
            {
                ClaudeExecutableInfo current = _locator.Resolve(_configuredPath);
                if (current.FailureReason is null && current.Identity is not null && MacClaudeBinaryIdentity.Parse(current.Identity).Key != identity)
                { Finish(state); ResetRetry(); return Failure("CLI_CANDIDATE_CHANGED"); }
            }
            ResetRetry();
            if (!pending && error.Message == "CLI_LEGACY_MIGRATION_REQUIRED") return Failure(error.Message, UsageAvailability.Unsupported);
            return Failure(pending ? "CLI_PREVIOUS_ATTEMPT_INCOMPLETE" : "CLI_COMPATIBILITY_STATE_UNAVAILABLE", UsageAvailability.Unsupported);
        }
        finally { ClaudeCliActiveSource.ConsoleExecutionGate.Release(); }
    }

    private void Report(string code)
    {
        if (code == "claude-launch-requested") { _cleanupConfirmed = false; _codeConfirmed = false; }
        if (code == "claude-session-started") { _cleanupConfirmed = false; _codeConfirmed = true; }
        if (code == "claude-cleanup-clean") _cleanupConfirmed = true;
        if (code == "claude-cleanup-failed") _cleanupConfirmed = false;
        if (code.StartsWith("claude-vt-rejected:", StringComparison.Ordinal)) _vtRejected = true;
        if (code == "claude-unknown-option") _unknownOption = true;
        if (code == "claude-identity-mismatch") _identityMismatch = true;
    }

    private ClaudeUsageObservation Stop(ClaudeCompatibilityState state, string identity, string reason)
    { state.Blocks.Add(new(identity, "", reason, DateTimeOffset.UtcNow)); Finish(state); ResetRetry(); return Failure(reason, UsageAvailability.Unsupported); }
    private void Finish(ClaudeCompatibilityState state)
    { state.Pending = null; _store.Write(state); }
    private void Backoff(string reason)
    {
        lock (_sync)
        { _lastFailure = reason; _next = _time.GetUtcNow().AddMinutes(RetryMinutes[Math.Min(_failures++, 4)]); }
    }
    private static ClaudeUsageObservation Failure(string reason, UsageAvailability availability = UsageAvailability.Unavailable) =>
        new(ClaudeUsageSourceKind.CliScreen, UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with { Availability = availability, Reason = reason });
    private sealed class CandidateLocator(ClaudeExecutableInfo candidate) : IClaudeExecutableLocator
    { public ClaudeExecutableInfo Resolve(string? path) => candidate; }
}
