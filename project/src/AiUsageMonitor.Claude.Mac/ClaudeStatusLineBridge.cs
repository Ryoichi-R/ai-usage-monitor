using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using AiUsageMonitor.Claude.StatusLine;

namespace AiUsageMonitor.Claude.Mac;

[SupportedOSPlatform("macos")]
public static class ClaudeStatusLineBridge
{
    private static readonly JsonSerializerOptions SetupOptions = new() { WriteIndented = true };
    public static async Task RunAsync(Stream input, string? socketPath = null)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await TransferAsync(input, socketPath ?? LocalSocketSecurity.DefaultPath, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
        catch (Exception) { } // statusLine must never break the caller or write diagnostics to its terminal.
    }

    private static async Task TransferAsync(Stream input, string path, CancellationToken token)
    {
        byte[] bytes = new byte[ClaudeStatusLineParser.MaximumPayloadBytes + 1]; int length = 0, read;
        while (length < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(length), token).ConfigureAwait(false)) > 0) length += read;
        if (length == 0 || length == bytes.Length) return;
        byte[]? payload = Minimize(bytes.AsSpan(0, length));
        if (payload is null || !LocalSocketSecurity.Valid(Path.GetDirectoryName(path)!, true) || !LocalSocketSecurity.Valid(path, false)) return;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectDeadline.CancelAfter(TimeSpan.FromMilliseconds(100));
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), connectDeadline.Token).ConfigureAwait(false);
        if (!LocalSocketSecurity.SameUser(socket)) return;
        int sent = 0;
        while (sent < payload.Length) sent += await socket.SendAsync(payload.AsMemory(sent), SocketFlags.None, token).ConfigureAwait(false);
        socket.Shutdown(SocketShutdown.Send);
    }

    internal static byte[]? Minimize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > ClaudeStatusLineParser.MaximumPayloadBytes) return null;
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray()); var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            JsonElement? limits = root.TryGetProperty("rate_limits", out var r) ? r : null;
            byte[] result = JsonSerializer.SerializeToUtf8Bytes(new { protocol = 1, version, rate_limits = limits });
            return result.Length <= ClaudeStatusLineParser.MaximumPayloadBytes ? result : null;
        }
        catch (JsonException) { return null; }
    }

    public static string CreateSetupExample(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath)) throw new ArgumentException("BRIDGE_PATH_MUST_BE_ABSOLUTE", nameof(executablePath));
        string command = "'" + executablePath.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return JsonSerializer.Serialize(new { statusLine = new { type = "command", command, refreshInterval = 10 } }, SetupOptions);
    }
}
