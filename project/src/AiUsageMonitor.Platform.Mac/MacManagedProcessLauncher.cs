using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>専用native helperが生成前のgroup・追跡登録と、親切断時の回収を所有する。</summary>
[SupportedOSPlatform("macos")]
public sealed class MacManagedProcessLauncher(string helperPath, string journalRoot, bool usePty = false, bool verifyClaude = false, Func<string?>? expectedIdentity = null) : IManagedProcessLauncher
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
        bool mismatchClean = false;
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
            bool versioned = verifyClaude && expectedIdentity is not null;
            info.ArgumentList.Add(versioned ? (usePty ? "--claude-pty-v2" : "--claude-v2") :
                verifyClaude ? (usePty ? "--claude-pty" : "--claude") : (usePty ? "--pty" : "--run"));
            info.ArgumentList.Add(journal);
            info.ArgumentList.Add(socketPath);
            if (versioned) info.ArgumentList.Add(expectedIdentity!() ?? throw new IOException("CLI_IDENTITY_MISSING"));
            info.ArgumentList.Add(startInfo.FileName);
            foreach (string argument in startInfo.ArgumentList) info.ArgumentList.Add(argument);
            info.Environment.Clear();
            foreach (var entry in startInfo.Environment) info.Environment[entry.Key] = entry.Value;
            helper = Process.Start(info) ?? throw new IOException("PROCESS_SUPERVISOR_START_FAILED");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            Task<Socket> accepted = listener.AcceptAsync(deadline.Token).AsTask();
            if (await Task.WhenAny(accepted, helper.WaitForExitAsync(deadline.Token)).ConfigureAwait(false) != accepted)
                throw new IOException(helper.ExitCode == 78 ? "CLI_IDENTITY_MISMATCH" : "PROCESS_SUPERVISOR_START_FAILED");
            control = await accepted.ConfigureAwait(false);
            byte[] ready = new byte[versioned ? 2 : 1];
            int received = 0;
            while (received < ready.Length)
            {
                int count = await control.ReceiveAsync(ready.AsMemory(received), SocketFlags.None, deadline.Token).ConfigureAwait(false);
                if (count == 0) break;
                received += count;
            }
            if (versioned && received == 2 && ready[0] == (byte)'2')
            {
                byte[] frame = new byte[13]; ready.CopyTo(frame, 0);
                int offset = 2;
                while (offset < frame.Length)
                {
                    int count = await control.ReceiveAsync(frame.AsMemory(offset), SocketFlags.None, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    offset += count;
                }
                mismatchClean = offset == 13 && frame[1] == (byte)'C' && frame[11] == 0 && frame[12] == (byte)'D' &&
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(7, 4)) == 78;
            }
            if (received != ready.Length || ready[0] != (byte)'R' || (versioned && ready[1] != (byte)'2'))
                throw new IOException("PROCESS_SUPERVISOR_NOT_READY");
            cancellationToken.ThrowIfCancellationRequested();
            return new Session(helper, control, helperPath, journal, versioned);
        }
        catch (Exception failure)
        {
            control?.Dispose();
            int? startupExit = null;
            if (helper is not null)
            {
                try
                {
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await helper.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { helper.Kill(); await helper.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { if (helper.HasExited) startupExit = helper.ExitCode; helper.Dispose(); }
                await SweepDirectoryAsync(helperPath, journal, CancellationToken.None).ConfigureAwait(false);
            }
            if (startupExit == 78) throw new MacProcessStartException("CLI_IDENTITY_MISMATCH", control is null || mismatchClean, failure);
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
        private readonly bool _versioned;

        public Session(Process helper, Socket control, string helperPath, string journal, bool versioned)
        {
            _helper = helper;
            _control = control;
            _versioned = versioned;
            _monitor = MonitorAsync(helperPath, journal);
        }

        public StreamReader StandardOutput => _helper.StandardOutput;
        public StreamReader StandardError => _helper.StandardError;
        public StreamWriter StandardInput => _helper.StandardInput;
        public ManagedProcessOutcome Outcome { get; private set; }
        public int? ExitCode { get; private set; }
        public int? TerminationSignal { get; private set; }
        public int? SupervisorError { get; private set; }

        private async Task MonitorAsync(string helperPath, string journal)
        {
            await _helper.WaitForExitAsync().ConfigureAwait(false);
            // A killed helper cannot clean up. The surviving owner immediately sweeps verified identities.
            await SweepDirectoryAsync(helperPath, journal, CancellationToken.None).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // Half-close requests termination; the helper answers with one outcome byte after cleanup.
            try { _control.Shutdown(SocketShutdown.Send); }
            catch (Exception error) when (error is SocketException or ObjectDisposedException) { }
            bool killed = false;
            try
            {
                try { await _monitor.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    killed = true;
                    if (!_helper.HasExited) _helper.Kill();
                    await _monitor.WaitAsync(TimeSpan.FromSeconds(9)).ConfigureAwait(false);
                }
                Outcome = killed ? ManagedProcessOutcome.SupervisionFailed : await ReadOutcomeAsync().ConfigureAwait(false);
            }
            finally
            {
                _control.Dispose();
                _helper.Dispose();
            }
        }

        private async Task<ManagedProcessOutcome> ReadOutcomeAsync()
        {
            // The helper has exited, so the byte is either buffered or the peer is closed.
            byte[] outcome = new byte[_versioned ? 13 : 1];
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                int length = 0;
                while (length < outcome.Length)
                {
                    int count = await _control.ReceiveAsync(outcome.AsMemory(length), SocketFlags.None, deadline.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    length += count;
                }
                if (length != outcome.Length) return ManagedProcessOutcome.SupervisionFailed;
                int offset = 0;
                if (_versioned)
                {
                    byte[] extra = new byte[1];
                    if (await _control.ReceiveAsync(extra, SocketFlags.None, deadline.Token).ConfigureAwait(false) != 0 ||
                        outcome[0] != (byte)'2' || outcome[11] != 1 || outcome[12] != (byte)'R')
                        return ManagedProcessOutcome.SupervisionFailed;
                    int status = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(outcome.AsSpan(3, 4));
                    SupervisorError = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(outcome.AsSpan(7, 4));
                    if (outcome[2] == (byte)'E' && status is >= 0 and <= 255) ExitCode = status;
                    else if (outcome[2] == (byte)'S' && status is > 0 and < 128) TerminationSignal = status;
                    else return ManagedProcessOutcome.SupervisionFailed;
                    if (outcome[1] == (byte)'E') return ManagedProcessOutcome.DescendantEscaped;
                    if (SupervisorError != 0) return ManagedProcessOutcome.SupervisionFailed;
                    offset = 1;
                }
                else if (_helper.ExitCode == 0) ExitCode = 0;
                return outcome[offset] switch
                {
                    (byte)'C' => ManagedProcessOutcome.Clean,
                    (byte)'E' => ManagedProcessOutcome.DescendantEscaped,
                    _ => ManagedProcessOutcome.SupervisionFailed,
                };
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException or ObjectDisposedException)
            { return ManagedProcessOutcome.SupervisionFailed; }
        }
    }
}

/// <summary>Only a versioned native pre-resume mismatch can prove cleanup despite startup failure.</summary>
public sealed class MacProcessStartException(string reason, bool cleanupConfirmed, Exception inner) : IOException(reason, inner)
{
    public bool CleanupConfirmed { get; } = cleanupConfirmed;
}
