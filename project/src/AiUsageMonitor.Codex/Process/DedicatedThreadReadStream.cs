using System.Threading.Channels;

namespace AiUsageMonitor.Codex.Process;

/// <summary>
/// 子プロセスのパイプを専用スレッドで同期的に読み、非同期の読み取り側へ渡す読み取り専用stream。
/// Windowsのリダイレクト用匿名パイプは非同期I/Oに対応しないため、そのままReadAsyncすると
/// データが届くまでスレッドプールのスレッドを1本占有する。常駐する子プロセスの標準出力・標準エラーで
/// これが積み重なるとスレッドプールが枯渇し、起動タイマーや応答の続き処理が数秒止まる。
/// </summary>
/// <remarks>
/// 内側のstreamは所有しない（Processが閉じる）。取り消された読み取りは待機をやめるだけで、
/// 専用スレッドはパイプがEOFまたはエラーになった時点で終了する。
/// </remarks>
internal sealed class DedicatedThreadReadStream : Stream
{
    private const int ChunkBytes = 4096;
    private readonly Stream _inner;
    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
    private byte[]? _current;
    private int _offset;

    public DedicatedThreadReadStream(Stream inner, string threadName)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanRead) throw new ArgumentException("The stream must be readable.", nameof(inner));
        _inner = inner;
        new Thread(Pump) { IsBackground = true, Name = threadName }.Start();
    }

    private void Pump()
    {
        try
        {
            byte[] buffer = new byte[ChunkBytes];
            int read;
            while ((read = _inner.Read(buffer, 0, buffer.Length)) > 0)
            {
                // Bounded hand-off: a slow consumer blocks only this dedicated thread.
                _chunks.Writer.WriteAsync(buffer.AsSpan(0, read).ToArray()).AsTask().Wait();
            }
            _chunks.Writer.TryComplete();
        }
        catch (AggregateException error) when (error.InnerException is ChannelClosedException) { }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            _chunks.Writer.TryComplete(error);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty) return 0;
        while (_current is null || _offset == _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return 0;
            if (_chunks.Reader.TryRead(out byte[]? chunk))
            {
                _current = chunk;
                _offset = 0;
            }
        }

        int count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _chunks.Writer.TryComplete();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
