using System.Text;
using System.Threading.Channels;
using AiUsageMonitor.Codex.Protocol;

namespace AiUsageMonitor.Codex.Tests;

public sealed class JsonRpcConnectionTests
{
    [Fact]
    public async Task MalformedAndNonObjectLinesDoNotStopConnection()
    {
        await using var input = new ControllableReadStream();
        await using var output = new MemoryStream();
        using var reader = new StreamReader(input, Encoding.UTF8);
        using var writer = new StreamWriter(output, Encoding.UTF8);
        await using var connection = new JsonRpcConnection(reader, writer);
        connection.Start();

        Task<System.Text.Json.JsonElement> request =
            connection.RequestAsync("test", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForOutputAsync(output);
        input.WriteLine("{");
        input.WriteLine("[]");
        input.WriteLine("""{"jsonrpc":"2.0","id":1,"result":{"ok":true}}""");

        System.Text.Json.JsonElement result = await request;
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task NotificationSubscriberFailureIsIsolated()
    {
        await using var input = new ControllableReadStream();
        await using var output = new MemoryStream();
        using var reader = new StreamReader(input, Encoding.UTF8);
        using var writer = new StreamWriter(output, Encoding.UTF8);
        await using var connection = new JsonRpcConnection(reader, writer);
        connection.Notification += _ => throw new InvalidOperationException("subscriber");
        connection.Start();

        Task<System.Text.Json.JsonElement> request =
            connection.RequestAsync("test", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForOutputAsync(output);
        input.WriteLine("""{"jsonrpc":"2.0","method":"changed"}""");
        input.WriteLine("""{"jsonrpc":"2.0","id":1,"result":42}""");

        Assert.Equal(42, (await request).GetInt32());
    }

    [Fact]
    public async Task EofFaultsPendingAndLaterRequestsFailFast()
    {
        await using var input = new ControllableReadStream();
        await using var output = new MemoryStream();
        using var reader = new StreamReader(input, Encoding.UTF8);
        using var writer = new StreamWriter(output, Encoding.UTF8);
        await using var connection = new JsonRpcConnection(reader, writer);
        connection.Start();

        Task<System.Text.Json.JsonElement> pending =
            connection.RequestAsync("test", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForOutputAsync(output);
        input.Complete();

        await Assert.ThrowsAsync<EndOfStreamException>(() => pending);
        await Assert.ThrowsAsync<EndOfStreamException>(
            () => connection.RequestAsync("later", null, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task MissingResultFaultsOnlyMatchingRequest()
    {
        await using var input = new ControllableReadStream();
        await using var output = new MemoryStream();
        using var reader = new StreamReader(input, Encoding.UTF8);
        using var writer = new StreamWriter(output, Encoding.UTF8);
        await using var connection = new JsonRpcConnection(reader, writer);
        connection.Start();

        Task<System.Text.Json.JsonElement> first =
            connection.RequestAsync("first", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        Task<System.Text.Json.JsonElement> second =
            connection.RequestAsync("second", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForOutputAsync(output);
        input.WriteLine("""{"jsonrpc":"2.0","id":1}""");
        input.WriteLine("""{"jsonrpc":"2.0","id":2,"result":"ok"}""");

        await Assert.ThrowsAsync<InvalidDataException>(() => first);
        Assert.Equal("ok", (await second).GetString());
    }

    private static async Task WaitForOutputAsync(MemoryStream output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (output.Length == 0)
            await Task.Delay(10, timeout.Token);
    }

    private sealed class ControllableReadStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;

        public void WriteLine(string value) =>
            _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(value + Environment.NewLine));

        public void Complete() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            while (_current is null || _offset == _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
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

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
