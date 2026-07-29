using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DiagnosticsProcess = System.Diagnostics.Process;

namespace AiUsageMonitor.Claude.Windows.Console;

public sealed class ConsoleHelperClient
{
    public const int MaximumResponseChars = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private readonly string _hostPath;
    private readonly string? _entryAssemblyPath;

    public ConsoleHelperClient(string? hostPath = null, string? entryAssemblyPath = null)
    {
        _hostPath = hostPath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("Current executable path is unavailable.");
        if (string.Equals(Path.GetFileNameWithoutExtension(_hostPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            _entryAssemblyPath = entryAssemblyPath ?? GetEntryAssemblyPath();
            if (string.IsNullOrWhiteSpace(_entryAssemblyPath))
                throw new InvalidOperationException("Entry assembly path is unavailable.");
        }
    }

    public Task<ConsoleHelperResponse> ReadAsync(
        int targetPid,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync("read", targetPid, timeout, cancellationToken);

    public Task<ConsoleHelperResponse> SendUsageAsync(
        int targetPid,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync("usage", targetPid, timeout, cancellationToken);

    public Task<ConsoleHelperResponse> SendEscapeAsync(
        int targetPid,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        ExecuteAsync("escape", targetPid, timeout, cancellationToken);

    private async Task<ConsoleHelperResponse> ExecuteAsync(
        string operation,
        int targetPid,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(_hostPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (_entryAssemblyPath is not null)
            startInfo.ArgumentList.Add(_entryAssemblyPath);
        startInfo.ArgumentList.Add(ClaudeConsoleHelper.Marker);
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add(targetPid.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using DiagnosticsProcess process = DiagnosticsProcess.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start console helper.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            string json = await ReadBoundedAsync(process.StandardOutput, timeoutSource.Token)
                .ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
                return Failure("HELPER_FAILED");
            return JsonSerializer.Deserialize<ConsoleHelperResponse>(json, SerializerOptions)
                ?? Failure("HELPER_INVALID_RESPONSE");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(process).ConfigureAwait(false);
            return Failure("HELPER_TIMEOUT");
        }
        catch (JsonException)
        {
            return Failure("HELPER_INVALID_RESPONSE");
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
            if (builder.Length + read > MaximumResponseChars)
                throw new JsonException("Helper response exceeded the limit.");
            builder.Append(buffer, 0, read);
        }
    }

    private static void TryKill(DiagnosticsProcess process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task StopAsync(DiagnosticsProcess process)
    {
        TryKill(process);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        { }
    }

    private static ConsoleHelperResponse Failure(string reason) =>
        new(false, reason, [], 0, 0, 0);

    private static string? GetEntryAssemblyPath() =>
        Assembly.GetEntryAssembly()?.GetName().Name is { } name
            ? Path.Combine(AppContext.BaseDirectory, name + ".dll")
            : null;
}
