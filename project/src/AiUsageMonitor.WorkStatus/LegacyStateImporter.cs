using System.Security.Cryptography;
using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus;

public static class LegacyStateImporter
{
    public static WorkStatusModel Read(string oldRoot, WorkStatusModel current, string? backupRoot = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        string state = WorkStatusPaths.Inside(oldRoot, Path.Combine(oldRoot, "state.json"));
        string monitorLock = WorkStatusPaths.Inside(oldRoot, Path.Combine(oldRoot, "monitor.lock"));
        using FileStream? held = File.Exists(monitorLock)
            ? new FileStream(monitorLock, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        if (new FileInfo(state).Length > WorkStatusStore.MaxBytes) throw new IOException("WORK_STATUS_LEGACY_TOO_LARGE");
        byte[] bytes = File.ReadAllBytes(state);
        string identity = "legacy:" + Convert.ToHexString(SHA256.HashData(bytes));
        WorkStatusModel candidate = WorkStatusStore.Copy(current);
        if (!candidate.Imports.Add(identity)) return candidate;
        using var document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("Version", out var version) || version.GetInt32() != 1 ||
            !root.TryGetProperty("Sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("Notices", out var notices) || notices.ValueKind != JsonValueKind.Object)
            throw new IOException("WORK_STATUS_LEGACY_SCHEMA_INVALID");
        foreach (JsonProperty surface in notices.EnumerateObject())
        {
            if (!Enum.TryParse(surface.Name, out WorkProvider provider) || !Enum.IsDefined(provider) ||
                surface.Value.ValueKind != JsonValueKind.Array) throw new IOException("WORK_STATUS_LEGACY_SURFACE_UNSUPPORTED");
            foreach (JsonElement old in surface.Value.EnumerateArray())
            {
                string session = old.GetProperty("SessionId").GetString() ?? "";
                string turn = old.GetProperty("TurnId").GetString() ?? "";
                if (session.Length is 0 or > 160 || turn.Length > 160) throw new IOException("WORK_STATUS_LEGACY_NOTICE_INVALID");
                string id = "legacy:" + HookNormalizer.Digest(old.GetProperty("Id").GetString() ?? "");
                if (!Enum.TryParse(old.GetProperty("State").GetString(), out WorkSessionState kind) ||
                    kind is not (WorkSessionState.Completed or WorkSessionState.Failed or WorkSessionState.Interrupted or WorkSessionState.StopCandidate))
                    throw new IOException("WORK_STATUS_LEGACY_NOTICE_INVALID");
                if (!candidate.Notices.Any(n => n.Id == id) && !candidate.AcknowledgedIds.Contains(id))
                    candidate.Notices.Add(new(id, provider, provider == WorkProvider.CodexDesktop ? HookNormalizer.Digest(session) : session,
                        HookNormalizer.Digest(turn), kind, old.GetProperty("At").GetDateTimeOffset().ToUniversalTime()));
            }
        }
        if (backupRoot is not null)
        {
            string backup = WorkStatusPaths.Inside(backupRoot, Path.Combine(backupRoot, identity.Replace(':', '-') + ".json"));
            Directory.CreateDirectory(backupRoot);
            if (!File.Exists(backup))
            {
                using var stream = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(true);
            }
            else if (!SHA256.HashData(File.ReadAllBytes(backup)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                throw new IOException("WORK_STATUS_LEGACY_BACKUP_MISMATCH");
        }
        // Progress is deliberately not imported: an old running state is not proof of current activity.
        return candidate;
    }
}
