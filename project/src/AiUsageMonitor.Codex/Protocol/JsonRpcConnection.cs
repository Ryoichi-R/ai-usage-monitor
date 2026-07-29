using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace AiUsageMonitor.Codex.Protocol;

public sealed class JsonRpcConnection(StreamReader reader, StreamWriter writer) : IAsyncDisposable
{
    private const int MaximumLineCharacters = 1_048_576;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<string> _writes = Channel.CreateBounded<string>(
        new BoundedChannelOptions(64) { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private Exception? _terminalException;
    private long _nextId;
    private Task? _readerTask;
    private Task? _writerTask;

    public event Action<string>? Notification;

    public void Start()
    {
        _readerTask ??= ReadLoopAsync(_lifetime.Token);
        _writerTask ??= WriteLoopAsync(_lifetime.Token);
    }

    public Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        return RequestWithOwnedCancellationAsync(method, parameters, timeoutCts);
    }

    public Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken) =>
        RequestCoreAsync(method, parameters, cancellationToken);

    private async Task<JsonElement> RequestWithOwnedCancellationAsync(
        string method,
        object? parameters,
        CancellationTokenSource timeoutCts)
    {
        using (timeoutCts)
        {
            return await RequestCoreAsync(method, parameters, timeoutCts.Token).ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> RequestCoreAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        ThrowIfTerminal();
        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
            throw new InvalidOperationException("Duplicate JSON-RPC request id.");

        try
        {
            Exception? terminal = Volatile.Read(ref _terminalException);
            if (terminal is not null && _pending.TryRemove(id, out TaskCompletionSource<JsonElement>? added))
            {
                added.TrySetException(terminal);
                throw terminal;
            }

            string json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
            await _writes.Writer.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public ValueTask NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfTerminal();
        return _writes.Writer.WriteAsync(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }),
            cancellationToken);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    throw new EndOfStreamException("Codex app-server closed stdout.");
                ProcessLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TransitionToTerminal(new ObjectDisposedException(nameof(JsonRpcConnection)));
        }
        catch (Exception exception) when (exception is IOException or EndOfStreamException)
        {
            TransitionToTerminal(exception);
        }
    }

    private void ProcessLine(string line)
    {
        if (line.Length > MaximumLineCharacters)
            return;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;

            if (root.TryGetProperty("id", out JsonElement idElement) &&
                idElement.TryGetInt64(out long id) &&
                _pending.TryGetValue(id, out TaskCompletionSource<JsonElement>? pending))
            {
                if (root.TryGetProperty("error", out JsonElement error))
                    pending.TrySetException(new InvalidOperationException($"JSON-RPC error: {error.GetRawText()}"));
                else if (root.TryGetProperty("result", out JsonElement result))
                    pending.TrySetResult(result.Clone());
                else
                    pending.TrySetException(new InvalidDataException("JSON-RPC response has no result or error."));
                return;
            }

            if (root.TryGetProperty("method", out JsonElement method) &&
                method.ValueKind == JsonValueKind.String)
            {
                try { Notification?.Invoke(method.GetString() ?? string.Empty); }
                catch (Exception) { }
            }
        }
        catch (JsonException)
        {
            // A malformed app-server line is isolated from the connection.
        }
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (string message in _writes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(message.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TransitionToTerminal(new ObjectDisposedException(nameof(JsonRpcConnection)));
        }
        catch (Exception exception) when (exception is IOException or ChannelClosedException)
        {
            TransitionToTerminal(exception);
        }
    }

    private void ThrowIfTerminal()
    {
        Exception? terminal = Volatile.Read(ref _terminalException);
        if (terminal is not null)
            throw terminal;
    }

    private void TransitionToTerminal(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _terminalException, exception, null) is not null)
            return;

        _writes.Writer.TryComplete(exception);
        foreach (KeyValuePair<long, TaskCompletionSource<JsonElement>> entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out TaskCompletionSource<JsonElement>? pending))
                pending.TrySetException(exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        TransitionToTerminal(new ObjectDisposedException(nameof(JsonRpcConnection)));
        _lifetime.Cancel();
        if (_readerTask is not null)
            await ObserveLoopAsync(_readerTask).ConfigureAwait(false);
        if (_writerTask is not null)
            await ObserveLoopAsync(_writerTask).ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private static async Task ObserveLoopAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
}
