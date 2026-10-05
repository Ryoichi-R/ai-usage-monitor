using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Claude.Mac;

[SupportedOSPlatform("macos")]
public sealed partial class MacClaudeCapabilityProbe(
    IManagedProcessLauncher launcher, ClaudeLaunchPolicy policy, IClaudeWorkspaceProvisioner workspace, ClaudeActiveQuarantine? quarantine = null)
{
    public async Task<ClaudeCliCapabilities> ProbeAsync(string executable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // 離脱を検出したversionは、ownerが再検証するまで能力確認の起動すら行わない（D12）。
            if (quarantine?.IsActive() == true) return new(false, null, ClaudeScreenFailureCode.DescendantEscaped.ToReasonCode());
            string folder = workspace.EnsureWorkspace();
            (string version, ClaudeScreenFailureCode? versionFailure) = await RunAsync(executable, folder, "--version", deadline.Token).ConfigureAwait(false);
            if (versionFailure is { } failure) return new(false, null, failure.ToReasonCode());
            if (!VersionPattern().IsMatch(version.Trim())) return new(false, null, "CLI_VERSION_REVALIDATION_REQUIRED");
            (string help, ClaudeScreenFailureCode? helpFailure) = await RunAsync(executable, folder, "--help", deadline.Token).ConfigureAwait(false);
            if (helpFailure is { } helpError) return new(false, null, helpError.ToReasonCode());
            string[] flags = ["--setting-sources", "--settings", "--tools", "--no-chrome", "--strict-mcp-config", "--safe-mode", "--ax-screen-reader"];
            return flags.All(flag => help.Contains(flag, StringComparison.Ordinal))
                ? new(true, ClaudeLaunchPolicy.VerifiedVersion, null) : new(false, null, "REQUIRED_FLAG_MISSING");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, null, "CAPABILITY_TIMEOUT"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return new(false, null, "CAPABILITY_FAILED"); }
    }

    private async Task<(string Output, ClaudeScreenFailureCode? Failure)> RunAsync(string executable, string folder, string argument, CancellationToken token)
    {
        var session = await launcher.StartAsync(policy.CreateStartInfo(executable, folder, argument), token).ConfigureAwait(false);
        string text;
        await using (session.ConfigureAwait(false))
        {
            Task<string> output = ReadAsync(session.StandardOutput, token);
            Task<string> error = ReadAsync(session.StandardError, token);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            text = await output.ConfigureAwait(false);
        }
        // 出力が正常でも、終了時の後始末で離脱・回収失敗が判明したら能力確認を不成立にする。
        ClaudeScreenFailureCode? failure = MacClaudeScreenSession.ToFailure(session.Outcome);
        if (failure == ClaudeScreenFailureCode.DescendantEscaped) quarantine?.Record();
        return (text, failure);
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

    [GeneratedRegex(@"^2\.1\.285(?:\s+\(Claude Code\))?$")]
    private static partial Regex VersionPattern();
}
