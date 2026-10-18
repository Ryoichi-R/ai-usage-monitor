using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Mac;

namespace AiUsageMonitor.Claude.Mac;

[SupportedOSPlatform("macos")]
public sealed partial class MacClaudeCapabilityProbe(
    IManagedProcessLauncher launcher, ClaudeLaunchPolicy policy, IClaudeWorkspaceProvisioner workspace,
    ClaudeActiveQuarantine? quarantine = null, Action<ManagedProcessOutcome>? completed = null, Func<string?>? cacheIdentity = null, Action? starting = null, Action? launched = null)
{
    private static readonly ConcurrentDictionary<string, string> Versions = new(StringComparer.Ordinal);

    public async Task<ClaudeCliCapabilities> ProbeAsync(string executable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            if (!policy.AllowsLaunch()) return new(false, null, "MANAGED_SETTINGS_PRESENT");
            if (quarantine?.IsActive() == true) return new(false, null, ClaudeScreenFailureCode.DescendantEscaped.ToReasonCode());
            string? key = cacheIdentity?.Invoke();
            if (key is not null && Versions.TryGetValue(key, out string? cached)) return new(true, cached, null);
            starting?.Invoke();
            var session = await launcher.StartAsync(policy.CreateStartInfo(executable, workspace.EnsureWorkspace(), "--version"), deadline.Token).ConfigureAwait(false);
            launched?.Invoke();
            string text;
            try
            {
                Task<string> output = ReadAsync(session.StandardOutput, deadline.Token);
                Task<string> error = ReadAsync(session.StandardError, deadline.Token);
                await Task.WhenAll(output, error).ConfigureAwait(false);
                text = await output.ConfigureAwait(false);
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
                completed?.Invoke(session.Outcome);
            }
            ClaudeScreenFailureCode? failure = MacClaudeScreenSession.ToFailure(session.Outcome);
            if (failure == ClaudeScreenFailureCode.DescendantEscaped) quarantine?.Record();
            if (failure is not null) return new(false, null, failure.Value.ToReasonCode());
            if (session.ExitCode != 0 || session.SupervisorError is not (null or 0)) return new(false, null, "CLI_VERSION_COMMAND_FAILED");
            Match match = VersionPattern().Match(text.Trim());
            string version = match.Success ? match.Groups[1].Value : "unknown";
            if (key is not null) Versions[key] = version;
            return new(true, version, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, null, "CAPABILITY_TIMEOUT"); }
        catch (MacProcessStartException error)
        {
            completed?.Invoke(error.CleanupConfirmed ? ManagedProcessOutcome.Clean : ManagedProcessOutcome.SupervisionFailed);
            return new(false, null, "CLI_IDENTITY_MISMATCH");
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return new(false, null, error.Message == "CLI_IDENTITY_MISMATCH" ? "CLI_IDENTITY_MISMATCH" : "CAPABILITY_FAILED"); }
    }

    private static async Task<string> ReadAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder(); char[] buffer = new char[4096];
        int length;
        while ((length = await reader.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + length > ClaudeCliCapabilityProbe.MaximumOutputChars) throw new IOException("CAPABILITY_OUTPUT_LIMIT");
            text.Append(buffer, 0, length);
        }
        return text.ToString();
    }

    [GeneratedRegex(@"^(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?:\s+\(Claude Code\))?$")]
    private static partial Regex VersionPattern();
}
