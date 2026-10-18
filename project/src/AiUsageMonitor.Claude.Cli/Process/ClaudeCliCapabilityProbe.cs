using System.Diagnostics;
using System.Text;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace AiUsageMonitor.Claude.Cli;

public sealed record ClaudeCliCapabilities(
    bool Supported,
    string? Version,
    string? FailureReason);

public sealed class ClaudeCliCapabilityProbe
{
    public const int MaximumOutputChars = 128 * 1024;
    private static readonly string[] RequiredFlags =
    [
        "--setting-sources",
        "--settings",
        "--tools",
        "--no-chrome",
        "--strict-mcp-config",
        "--safe-mode",
        "--ax-screen-reader",
    ];

    public static async Task<ClaudeCliCapabilities> ProbeAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            string help = await RunAsync(executablePath, "--help", timeout, cancellationToken)
                .ConfigureAwait(false);
            if (RequiredFlags.Any(flag => !help.Contains(flag, StringComparison.Ordinal)))
                return new(false, null, "REQUIRED_FLAG_MISSING");
            string version = (await RunAsync(executablePath, "--version", timeout, cancellationToken)
                .ConfigureAwait(false)).Trim();
            return new(true, version.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            return new(false, null, "CAPABILITY_TIMEOUT");
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return new(false, null, "CAPABILITY_FAILED");
        }
    }

    private static async Task<string> RunAsync(
        string executablePath,
        string argument,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(argument);
        using DiagnosticsProcess process = DiagnosticsProcess.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Claude CLI.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<string> outputTask = ReadBoundedAsync(process.StandardOutput, timeoutSource.Token);
        Task<string> errorTask = ReadBoundedAsync(process.StandardError, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Claude CLI failed." : "Claude CLI reported an error.");
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException();
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        char[] buffer = new char[4096];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) return builder.ToString();
            if (builder.Length + read > MaximumOutputChars)
                throw new InvalidOperationException("Claude CLI output exceeded the limit.");
            builder.Append(buffer, 0, read);
        }
    }

    private static void TryKill(DiagnosticsProcess process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
