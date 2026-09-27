using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>専用native helperが生成前のgroup・追跡登録と、親切断時の回収を所有する。</summary>
[SupportedOSPlatform("macos")]
public sealed class MacManagedProcessLauncher(string helperPath, string journalRoot, bool usePty = false, bool verifyClaude = false) : IManagedProcessLauncher
{
    public async Task<IManagedProcessSession> StartAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(startInfo.FileName) || !File.Exists(helperPath))
            throw new IOException("PROCESS_SUPERVISOR_UNAVAILABLE");
        await SweepAsync(cancellationToken).ConfigureAwait(false);
        string journal = Path.Combine(journalRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(journal, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // sockaddr_un has a short path limit on macOS. A private, random directory prevents other users connecting.
        string socketDirectory = Path.Combine("/tmp", "aiusage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string socketPath = Path.Combine(socketDirectory, "control");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Process? helper = null;
        Socket? control = null;
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var info = new ProcessStartInfo(helperPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = startInfo.WorkingDirectory,
            };
            info.ArgumentList.Add(verifyClaude ? (usePty ? "--claude-pty" : "--claude") : (usePty ? "--pty" : "--run"));
            info.ArgumentList.Add(journal);
            info.ArgumentList.Add(socketPath);
            info.ArgumentList.Add(startInfo.FileName);
            foreach (string argument in startInfo.ArgumentList) info.ArgumentList.Add(argument);
            info.Environment.Clear();
            foreach (var entry in startInfo.Environment) info.Environment[entry.Key] = entry.Value;
            helper = Process.Start(info) ?? throw new IOException("PROCESS_SUPERVISOR_START_FAILED");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            control = await listener.AcceptAsync(deadline.Token).ConfigureAwait(false);
            byte[] ready = new byte[1];
            if (await control.ReceiveAsync(ready, SocketFlags.None, deadline.Token).ConfigureAwait(false) != 1 || ready[0] != (byte)'R')
                throw new IOException("PROCESS_SUPERVISOR_NOT_READY");
            cancellationToken.ThrowIfCancellationRequested();
            return new Session(helper, control, helperPath, journal);
        }
        catch
        {
            control?.Dispose();
            if (helper is not null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await helper.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { helper.Kill(); await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { helper.Dispose(); }
                await SweepDirectoryAsync(helperPath, journal, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            listener.Dispose();
            File.Delete(socketPath);
            Directory.Delete(socketDirectory);
        }
    }

    public async Task SweepAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(journalRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (string directory in Directory.EnumerateDirectories(journalRoot))
        {
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _))
                await SweepDirectoryAsync(helperPath, directory, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task SweepDirectoryAsync(string helperPath, string directory, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(helperPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("--sweep");
        info.ArgumentList.Add(directory);
        using var process = Process.Start(info) ?? throw new IOException("PROCESS_SWEEP_START_FAILED");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        // 75 means an active helper owns the directory lock. Never sweep an active session.
        if (process.ExitCode is not (0 or 75)) throw new IOException("PROCESS_SWEEP_FAILED");
        if (process.ExitCode == 0 && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            Directory.Delete(directory);
    }

    private sealed class Session : IManagedProcessSession
    {
        private readonly Process _helper;
        private readonly Socket _control;
        private readonly Task _monitor;
        private int _disposed;

        public Session(Process helper, Socket control, string helperPath, string journal)
        {
            _helper = helper;
            _control = control;
            _monitor = MonitorAsync(helperPath, journal);
        }

        public StreamReader StandardOutput => _helper.StandardOutput;
        public StreamReader StandardError => _helper.StandardError;
        public StreamWriter StandardInput => _helper.StandardInput;

        private async Task MonitorAsync(string helperPath, string journal)
        {
            await _helper.WaitForExitAsync().ConfigureAwait(false);
            // A killed helper cannot clean up. The surviving owner immediately sweeps verified identities.
            await SweepDirectoryAsync(helperPath, journal, CancellationToken.None).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _control.Dispose();
            try
            {
                try { await _monitor.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    if (!_helper.HasExited) _helper.Kill();
                    await _monitor.WaitAsync(TimeSpan.FromSeconds(9)).ConfigureAwait(false);
                }
            }
            finally { _helper.Dispose(); }
        }
    }
}
