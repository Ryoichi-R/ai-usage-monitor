using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Claude.Windows.Console;
using AiUsageMonitor.Claude.Windows.Process;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Windows.Cli;

/// <summary>
/// 公式Claude CLIを隠しconsoleで起動し、/usage後のstatusLineを専用pipeで受け取るB1 source。
/// </summary>
public sealed class ClaudeCliActiveSource : IClaudeUsageSource
{
    private static readonly SemaphoreSlim ConsoleExecutionGate = new(1, 1);
    private readonly string? _configuredExecutablePath;
    private readonly ClaudeWorkspaceProvisioner _workspace;
    private readonly ConsoleHelperClient _helper;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _helperTimeout;
    private readonly Func<string?, ClaudeExecutableInfo> _resolveExecutable;
    private readonly Func<string, TimeSpan, CancellationToken, Task<ClaudeCliCapabilities>> _probeCapabilities;

    public ClaudeCliActiveSource(
        string? configuredExecutablePath,
        string bridgePath,
        TimeSpan startupTimeout,
        ClaudeWorkspaceProvisioner? workspace = null,
        ConsoleHelperClient? helper = null,
        Func<string?, ClaudeExecutableInfo>? resolveExecutable = null,
        Func<string, TimeSpan, CancellationToken, Task<ClaudeCliCapabilities>>? probeCapabilities = null)
    {
        _configuredExecutablePath = configuredExecutablePath;
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);
        _startupTimeout = startupTimeout;
        _helperTimeout = TimeSpan.FromSeconds(Math.Clamp(startupTimeout.TotalSeconds / 3, 2, 10));
        _workspace = workspace ?? new ClaudeWorkspaceProvisioner();
        _helper = helper ?? new ConsoleHelperClient();
        _resolveExecutable = resolveExecutable ?? ClaudeExecutableLocator.Resolve;
        _probeCapabilities = probeCapabilities ?? ClaudeCliCapabilityProbe.ProbeAsync;
    }

    public string WorkspacePath => _workspace.WorkspacePath;

    public async Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows())
            return Observation(Failure(startedAt, UsageAvailability.Unsupported, "UNSUPPORTED_PLATFORM"));

        ClaudeExecutableInfo executable = _resolveExecutable(_configuredExecutablePath);
        if (executable.FailureReason is not null)
        {
            UsageAvailability availability = executable.FailureReason == "CLAUDE_NOT_INSTALLED"
                ? UsageAvailability.NotInstalled
                : UsageAvailability.Unsupported;
            return Observation(Failure(startedAt, availability, executable.FailureReason));
        }

        ClaudeCliCapabilities capabilities = await _probeCapabilities(
            executable.Path,
            TimeSpan.FromSeconds(Math.Clamp(_startupTimeout.TotalSeconds, 5, 30)),
            cancellationToken).ConfigureAwait(false);
        if (!capabilities.Supported)
            return Observation(Failure(
                DateTimeOffset.UtcNow,
                UsageAvailability.Unsupported,
                capabilities.FailureReason,
                capabilities.Version ?? executable.Version));

        await ConsoleExecutionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunSessionAsync(
                executable.Path,
                capabilities.Version ?? executable.Version,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ConsoleExecutionGate.Release();
        }
    }

    private async Task<ClaudeUsageObservation> RunSessionAsync(
        string executablePath,
        string? version,
        CancellationToken cancellationToken)
    {
        string workspacePath;
        try
        {
            workspacePath = _workspace.EnsureWorkspace();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException)
        {
            return Observation(Failure(
                DateTimeOffset.UtcNow,
                UsageAvailability.Error,
                "WORKSPACE_PROVISION_FAILED",
                version));
        }

        HiddenConsoleSession session;
        try
        {
            session = HiddenConsoleSession.Start(executablePath, workspacePath);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return Observation(Failure(
                DateTimeOffset.UtcNow,
                UsageAvailability.Error,
                "PROCESS_START_FAILED",
                version));
        }

        await using (session)
        {
            try
            {
                UsageSnapshot result = await WaitUntilReadyAndReadUsageAsync(
                    session,
                    version,
                    cancellationToken).ConfigureAwait(false);
                return Observation(
                    result,
                    result.Availability == UsageAvailability.Available
                        ? ClaudeUsageSourceKind.CliScreen
                        : ClaudeUsageSourceKind.StatusLineActive);
            }
            finally
            {
                try
                {
                    await _helper.SendEscapeAsync(
                        session.Process.Id,
                        _helperTimeout,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidOperationException)
                { }
            }
        }
    }

    private async Task<UsageSnapshot> WaitUntilReadyAndReadUsageAsync(
        HiddenConsoleSession session,
        string? version,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _startupTimeout;
        bool usageSent = false;
        UsageSnapshot? lastIncompleteUsageScreen = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (session.Process.HasExited)
                return Failure(DateTimeOffset.UtcNow, UsageAvailability.Unavailable, "PROCESS_EXITED", version);

            ConsoleHelperResponse screen = await _helper.ReadAsync(
                session.Process.Id,
                _helperTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!screen.Ok)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                continue;
            }

            ClaudeCliScreenSignature signature =
                ClaudeCliScreenStateMachine.Classify(screen.Lines);
            switch (signature)
            {
                case ClaudeCliScreenSignature.TrustPrompt:
                case ClaudeCliScreenSignature.SetupScreen:
                    return Failure(DateTimeOffset.UtcNow, UsageAvailability.Setup, "CLAUDE_TRUST_REQUIRED", version);
                case ClaudeCliScreenSignature.SignedOut:
                    return Failure(DateTimeOffset.UtcNow, UsageAvailability.SignedOut, "CLAUDE_SIGNED_OUT", version);
                case ClaudeCliScreenSignature.Ready:
                    if (usageSent)
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    ConsoleHelperResponse sent = await _helper.SendUsageAsync(
                        session.Process.Id,
                        _helperTimeout,
                        cancellationToken).ConfigureAwait(false);
                    if (!sent.Ok)
                        return Failure(
                            DateTimeOffset.UtcNow,
                            UsageAvailability.Error,
                            sent.Reason ?? "INPUT_FAILED",
                            version);
                    usageSent = true;
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    break;
                case ClaudeCliScreenSignature.UsageScreen:
                    if (!usageSent)
                    {
                        return Failure(
                            DateTimeOffset.UtcNow,
                            UsageAvailability.Unsupported,
                            "UNEXPECTED_USAGE_SCREEN",
                            version);
                    }
                    UsageSnapshot parsed = ClaudeCliUsageScreenParser.Parse(
                        screen.Lines,
                        DateTimeOffset.UtcNow,
                        version);
                    if (parsed.Availability == UsageAvailability.Available ||
                        !string.Equals(
                            parsed.Reason,
                            "USAGE_SCREEN_PARSE_FAILED",
                            StringComparison.Ordinal))
                    {
                        return parsed;
                    }
                    // Claude Codeは/usageを段階描画する。見出しだけが先に現れた
                    // incomplete screenを確定失敗にせず、同じbounded session内で再読込する。
                    lastIncompleteUsageScreen = parsed;
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    break;
                case ClaudeCliScreenSignature.Unknown:
                default:
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        return lastIncompleteUsageScreen ??
            Failure(DateTimeOffset.UtcNow, UsageAvailability.Unavailable, "READY_TIMEOUT", version);
    }

    private static ClaudeUsageObservation Observation(
        UsageSnapshot snapshot,
        ClaudeUsageSourceKind source = ClaudeUsageSourceKind.StatusLineActive) =>
        new(source, snapshot);

    private static UsageSnapshot Failure(
        DateTimeOffset now,
        UsageAvailability availability,
        string? reason,
        string? version = null) =>
        new(
            UsageProvider.Claude,
            now,
            now,
            availability,
            reason,
            null,
            [],
            null,
            false,
            null,
            version);
}
