using System.IO.Pipes;
using AiUsageMonitor.Claude.StatusLine;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Transport;

public sealed class ClaudeUsagePipeServer : IAsyncDisposable
{
    // Public compatibility identifier used by existing statusLine settings.
    public const string PipeName = "CodexUsageMonitor-Claude-v1";
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _pipeName;
    private Task? _listener;

    public event Action<UsageSnapshot>? ObservationReceived;

    public ClaudeUsagePipeServer(string pipeName = PipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
    }

    public void Start() => _listener ??= ListenAsync(_lifetime.Token);

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await using (pipe.ConfigureAwait(false))
            {
                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    byte[] chunk = new byte[4096];
                    bool payloadRejected = false;
                    int read;
                    while ((read = await pipe.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        if (buffer.Length + read > ClaudeStatusLineParser.MaximumPayloadBytes)
                        {
                            Publish(ClaudeStatusLineParser.Parse(
                                new byte[ClaudeStatusLineParser.MaximumPayloadBytes + 1],
                                DateTimeOffset.UtcNow));
                            payloadRejected = true;
                            break;
                        }
                        buffer.Write(chunk, 0, read);
                    }

                    if (!payloadRejected)
                        Publish(ClaudeStatusLineParser.Parse(buffer.ToArray(), DateTimeOffset.UtcNow));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
    }

    private void Publish(UsageSnapshot snapshot)
    {
        try { ObservationReceived?.Invoke(snapshot); }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_listener is not null)
        {
            try { await _listener.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}
