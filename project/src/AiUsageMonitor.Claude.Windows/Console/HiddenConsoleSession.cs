using System.Diagnostics;
using AiUsageMonitor.Windows.Process;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace AiUsageMonitor.Claude.Windows.Console;

public sealed class HiddenConsoleSession : IAsyncDisposable
{
    private readonly ProcessJobObject _job;
    private bool _disposed;

    private HiddenConsoleSession(DiagnosticsProcess process, ProcessJobObject job)
    {
        Process = process;
        _job = job;
    }

    public DiagnosticsProcess Process { get; }

    public static HiddenConsoleSession Start(
        string executablePath,
        string workingDirectory,
        string? settingsPath = null)
    {
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("--setting-sources");
        startInfo.ArgumentList.Add(string.Empty);
        if (settingsPath is not null)
        {
            startInfo.ArgumentList.Add("--settings");
            startInfo.ArgumentList.Add(settingsPath);
        }
        startInfo.ArgumentList.Add("--tools");
        startInfo.ArgumentList.Add(string.Empty);
        startInfo.ArgumentList.Add("--no-chrome");
        startInfo.ArgumentList.Add("--strict-mcp-config");
        startInfo.ArgumentList.Add("--safe-mode");
        startInfo.ArgumentList.Add("--ax-screen-reader");

        DiagnosticsProcess process = DiagnosticsProcess.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Claude CLI.");
        try
        {
            ProcessJobObject job = ProcessJobObject.Attach(process);
            return new(process, job);
        }
        catch
        {
            TryKill(process);
            process.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _job.Dispose();
        try
        {
            if (!Process.HasExited)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            TryKill(Process);
        }
        finally
        {
            Process.Dispose();
        }
    }

    private static void TryKill(DiagnosticsProcess process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
