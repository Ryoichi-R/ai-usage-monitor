using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.Platform;
using AiUsageMonitor.Platform.Windows.WorkStatus;
using AiUsageMonitor.WorkStatus;

namespace AiUsageMonitor.App.Windows;

internal sealed class ClaudeDesktopActivity(string userProfile, string? configDirectory = null,
    Func<int, long, bool>? isDesktopProcess = null)
{
    private const int MaxSessionFiles = 256;
    private const int MaxMetadataBytes = 8192;
    private readonly string _sessions = Path.Combine(
        Path.IsPathFullyQualified(configDirectory ?? "") ? configDirectory! : Path.Combine(userProfile, ".claude"), "sessions");

    public IReadOnlyDictionary<string, DateTimeOffset> ReadBusySince(IReadOnlyList<WorkSession> sessions, DateTimeOffset now)
    {
        var ended = sessions.Where(s => s.Provider == WorkProvider.ClaudeCode &&
            s.State is WorkSessionState.StopCandidate or WorkSessionState.Completed)
            .GroupBy(s => s.SessionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(s => s.At), StringComparer.Ordinal);
        var busy = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (ended.Count == 0 || !Directory.Exists(_sessions)) return busy;

        try
        {
            WorkStatusPaths.RejectLinks(_sessions);
            foreach (string path in Directory.EnumerateFiles(_sessions, "*.json").Take(MaxSessionFiles))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.None, CultureInfo.InvariantCulture,
                    out int pid) || pid <= 0) continue;
                ReadSession(path, pid, ended, busy, now);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return busy;
    }

    private void ReadSession(string path, int pid, Dictionary<string, DateTimeOffset> ended,
        Dictionary<string, DateTimeOffset> busy, DateTimeOffset now)
    {
        try
        {
            WorkStatusPaths.RejectLinks(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = file.Length;
            if (length is <= 0 or > MaxMetadataBytes) return;
            byte[] bytes = new byte[(int)length];
            file.ReadExactly(bytes);
            using var document = JsonDocument.Parse(bytes);
            JsonElement record = document.RootElement;
            if (record.ValueKind != JsonValueKind.Object ||
                !HasText(record, "entrypoint", "claude-desktop") || !HasText(record, "kind", "interactive") ||
                !HasText(record, "status", "busy") || ReadNumber(record, "pid") != pid ||
                !record.TryGetProperty("sessionId", out var id) || id.ValueKind != JsonValueKind.String ||
                id.GetString() is not { } sessionId || !Guid.TryParseExact(sessionId, "D", out _)) return;

            string hash = HookNormalizer.Digest(sessionId);
            long busySince = ReadNumber(record, "statusUpdatedAt");
            long processStart = ReadNumber(record, "procStart");
            if (!ended.TryGetValue(hash, out var stoppedAt) || busySince <= stoppedAt.ToUnixTimeMilliseconds() ||
                busySince > now.ToUnixTimeMilliseconds() || processStart <= 0 ||
                !(isDesktopProcess ?? IsLiveDesktopProcess)(pid, processStart)) return;

            DateTimeOffset startedAt = DateTimeOffset.FromUnixTimeMilliseconds(busySince);
            if (!busy.TryGetValue(hash, out var previous) || previous < startedAt) busy[hash] = startedAt;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private static bool HasText(JsonElement record, string name, string expected) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.ValueEquals(expected);

    private static long ReadNumber(JsonElement record, string name) =>
        record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out long number) ? number : 0;

    private static bool IsLiveDesktopProcess(int pid, long processStart)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase) &&
                process.StartTime.ToFileTimeUtc() == processStart &&
                WindowsDesktopOrigin.HasDesktopAncestor(DesktopApplication.Claude, pid);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        { return false; }
    }
}
