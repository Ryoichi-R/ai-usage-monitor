using System.Text;
using System.Text.RegularExpressions;
using AiUsageMonitor.Claude.Cli;
using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Claude.Mac;

public sealed partial class MacClaudeCapabilityProbe(IManagedProcessLauncher launcher, ClaudeLaunchPolicy policy, IClaudeWorkspaceProvisioner workspace)
{
    public async Task<ClaudeCliCapabilities> ProbeAsync(string executable, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            string folder = workspace.EnsureWorkspace();
            string version = (await RunAsync(executable, folder, "--version", deadline.Token).ConfigureAwait(false)).Trim();
            if (!VersionPattern().IsMatch(version)) return new(false, null, "CLI_VERSION_REVALIDATION_REQUIRED");
            string help = await RunAsync(executable, folder, "--help", deadline.Token).ConfigureAwait(false);
            string[] flags = ["--setting-sources", "--settings", "--tools", "--no-chrome", "--strict-mcp-config", "--safe-mode", "--ax-screen-reader"];
            return flags.All(flag => help.Contains(flag, StringComparison.Ordinal))
                ? new(true, ClaudeLaunchPolicy.VerifiedVersion, null) : new(false, null, "REQUIRED_FLAG_MISSING");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, null, "CAPABILITY_TIMEOUT"); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        { return new(false, null, "CAPABILITY_FAILED"); }
    }

    private async Task<string> RunAsync(string executable, string folder, string argument, CancellationToken token)
    {
        await using var session = await launcher.StartAsync(policy.CreateStartInfo(executable, folder, argument), token).ConfigureAwait(false);
        Task<string> output = ReadAsync(session.StandardOutput, token);
        Task<string> error = ReadAsync(session.StandardError, token);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        return await output.ConfigureAwait(false);
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

    [GeneratedRegex(@"^2\.1\.274(?:\s+\(Claude Code\))?$")]
    private static partial Regex VersionPattern();
}
