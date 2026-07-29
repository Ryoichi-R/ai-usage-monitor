using System.Diagnostics;
using System.Reflection;
using AiUsageMonitor.Codex.Protocol;

namespace AiUsageMonitor.Codex.Process;

public sealed class CodexAppServerProcess : IAsyncDisposable
{
    private readonly CancellationTokenSource _processLifetime = new();
    private System.Diagnostics.Process? _process;
    private JsonRpcConnection? _connection;
    private Task? _stderrTask;
    private IDisposable? _lifetimeGuard;
    private int _disposed;

    public JsonRpcConnection Connection =>
        _connection ?? throw new InvalidOperationException("App server is not started.");

    public Task StartAsync(
        string executablePath,
        TimeSpan startupTimeout,
        CancellationToken cancellationToken) =>
        StartAsync(executablePath, null, startupTimeout, null, cancellationToken);

    public async Task StartAsync(
        string executablePath,
        string? codexHomePath,
        TimeSpan startupTimeout,
        Func<System.Diagnostics.Process, IDisposable>? lifetimeGuardFactory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_process is not null)
            throw new InvalidOperationException("App server is already started.");

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("app-server");
        if (codexHomePath is not null)
            startInfo.Environment["CODEX_HOME"] = codexHomePath;
        _process = new System.Diagnostics.Process { StartInfo = startInfo };
        if (!_process.Start())
            throw new InvalidOperationException("Failed to start Codex app-server.");
        try
        {
            _lifetimeGuard = lifetimeGuardFactory?.Invoke(_process);
        }
        catch
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        _stderrTask = DrainStderrAsync(_process.StandardError, _processLifetime.Token);
        _connection = new JsonRpcConnection(_process.StandardOutput, _process.StandardInput);
        _connection.Start();

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCts.CancelAfter(startupTimeout);
        await _connection.RequestAsync(
            "initialize",
            new
            {
                clientInfo = new
                {
                    name = "ai-usage-monitor",
                    title = "AI Usage Monitor",
                    version = GetClientVersion(),
                }
            },
            startupCts.Token).ConfigureAwait(false);
        await _connection.NotifyAsync("initialized", new { }, startupCts.Token).ConfigureAwait(false);
    }

    internal static string GetClientVersion()
    {
        string? version = typeof(CodexAppServerProcess).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version;
    }

    private static async Task DrainStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[1024];
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) > 0) { }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);

        if (_process is not null)
        {
            try
            {
                _process.StandardInput.Close();
                using var exitTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                try { await _process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!_process.HasExited)
                        _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch (InvalidOperationException) { }
        }

        _processLifetime.Cancel();
        if (_stderrTask is not null)
            await _stderrTask.ConfigureAwait(false);
        _process?.Dispose();
        _lifetimeGuard?.Dispose();
        _processLifetime.Dispose();
    }
}
