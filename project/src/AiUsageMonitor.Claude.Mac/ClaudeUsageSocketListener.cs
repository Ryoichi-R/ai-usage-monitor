using System.Net.Sockets;
using System.Runtime.Versioning;
using AiUsageMonitor.Claude.StatusLine;
using AiUsageMonitor.Claude.Transport;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Mac;

[SupportedOSPlatform("macos")]
public sealed class ClaudeUsageSocketListener(string? socketPath = null) : IClaudeUsageListener
{
    private readonly string _path = socketPath ?? LocalSocketSecurity.DefaultPath;
    private readonly CancellationTokenSource _lifetime = new();
    private Socket? _listener;
    private Task? _task;
    private int _lock = -1, _disposed;
    public event Action<UsageSnapshot>? ObservationReceived;

    public void Start()
    {
        if (_task is not null) return;
        string parent = Path.GetDirectoryName(_path) ?? throw new IOException("SOCKET_PATH_INVALID");
        Directory.CreateDirectory(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _lock = LocalSocketSecurity.LockDirectory(parent);
        if (_lock < 0) throw new IOException("SOCKET_DIRECTORY_UNSAFE_OR_BUSY");
        try
        {
            int state = LocalSocketSecurity.Validate(_path, 0);
            if (state == 1) throw new IOException("SOCKET_PATH_UNSAFE");
            if (state == 0)
            {
                using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { probe.Connect(new UnixDomainSocketEndPoint(_path)); throw new IOException("SOCKET_ALREADY_ACTIVE"); }
                catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
                { if (!LocalSocketSecurity.Valid(_path, false)) throw new IOException("SOCKET_PATH_CHANGED"); File.Delete(_path); }
            }
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(_path));
            File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (!LocalSocketSecurity.Valid(parent, true) || !LocalSocketSecurity.Valid(_path, false)) throw new IOException("SOCKET_PATH_CHANGED");
            _listener.Listen(4);
            _task = ListenAsync(_lifetime.Token);
        }
        catch { _listener?.Dispose(); _ = LocalSocketSecurity.Close(_lock); _lock = -1; throw; }
    }

    private async Task ListenAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                using Socket peer = await _listener!.AcceptAsync(token).ConfigureAwait(false);
                if (!LocalSocketSecurity.SameUser(peer)) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    byte[] bytes = new byte[ClaudeStatusLineParser.MaximumPayloadBytes + 1]; int length = 0, read;
                    while (length < bytes.Length && (read = await peer.ReceiveAsync(bytes.AsMemory(length), SocketFlags.None, deadline.Token).ConfigureAwait(false)) > 0) length += read;
                    var snapshot = ClaudeStatusLineParser.Parse(bytes.AsSpan(0, length), DateTimeOffset.UtcNow);
                    try { ObservationReceived?.Invoke(snapshot); } catch (Exception) { }
                }
                catch (Exception error) when (error is OperationCanceledException or SocketException or IOException) { }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel(); _listener?.Dispose();
        if (_task is not null) await _task.ConfigureAwait(false);
        if (_lock >= 0)
        {
            if (LocalSocketSecurity.Valid(_path, false)) File.Delete(_path);
            _ = LocalSocketSecurity.Close(_lock);
        }
        _lifetime.Dispose();
    }
}
