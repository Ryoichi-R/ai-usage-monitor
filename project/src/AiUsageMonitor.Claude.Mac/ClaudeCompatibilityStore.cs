using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace AiUsageMonitor.Claude.Mac;

public sealed record ClaudeCompatibilityBlock(string BinaryId, string Contract, string Reason, DateTimeOffset DetectedAt, DateTimeOffset? ReleasedAt = null);
public sealed record ClaudeCompatibilityPending(Guid AttemptId, string BinaryId, DateTimeOffset StartedAt);
public sealed record ClaudePendingResolution(ClaudeCompatibilityPending Pending, DateTimeOffset ResolvedAt);
public sealed class ClaudeCompatibilityState
{
    [System.Text.Json.Serialization.JsonRequired]
    public int SchemaVersion { get; set; } = 1;
    public string? LastBinaryId { get; set; }
    public ClaudeCompatibilityPending? Pending { get; set; }
    [System.Text.Json.Serialization.JsonRequired]
    public List<ClaudeCompatibilityBlock> Blocks { get; set; } = [];
    public string? LegacyDigest { get; set; }
    public bool LegacyReleased { get; set; }
    public string? LegacyBinaryId { get; set; }
    public string? LegacyEvidenceDigest { get; set; }
    public List<ClaudePendingResolution> Resolutions { get; set; } = [];
}

/// <summary>Write-ahead safety state. A saved pending attempt is never cleared by restarting.</summary>
[SupportedOSPlatform("macos")]
public sealed class ClaudeCompatibilityStore(string directory, string? legacyPath = null)
{
    public string StatePath => Path.Combine(directory, "state.json");
    private string PendingPath => Path.Combine(directory, "pending.json");
    internal Action<string>? WriteCheckpoint { get; set; }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public ClaudeCompatibilityState Read()
    {
        if (Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null) CheckPrivate(new DirectoryInfo(directory));
        if (Directory.Exists(StatePath)) throw new IOException("CLI_COMPATIBILITY_STATE_INVALID");
        ClaudeCompatibilityState state = new();
        if (File.Exists(StatePath) || new FileInfo(StatePath).LinkTarget is not null)
        {
            CheckPrivate(new FileInfo(StatePath));
            if (new FileInfo(StatePath).Length > 65536) throw new IOException("CLI_COMPATIBILITY_STATE_INVALID");
            state = JsonSerializer.Deserialize<ClaudeCompatibilityState>(File.ReadAllBytes(StatePath), Options)
                ?? throw new IOException("CLI_COMPATIBILITY_STATE_INVALID");
            Validate(state);
        }
        if (legacyPath is not null && (File.Exists(legacyPath) || new FileInfo(legacyPath).LinkTarget is not null))
        {
            var info = new FileInfo(legacyPath);
            if (info.LinkTarget is not null || info.Length > 1024) throw new IOException("CLI_LEGACY_MIGRATION_REQUIRED");
            string digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyPath)));
            if (state.LegacyDigest != digest || (!state.LegacyReleased && state.LegacyBinaryId is null)) throw new IOException("CLI_LEGACY_MIGRATION_REQUIRED");
        }
        // Independent durable guard survives a failed final replace/directory sync.
        if (Directory.Exists(PendingPath)) throw new IOException("CLI_COMPATIBILITY_STATE_INVALID");
        if (File.Exists(PendingPath) || new FileInfo(PendingPath).LinkTarget is not null)
        {
            CheckPrivate(new FileInfo(PendingPath));
            if (new FileInfo(PendingPath).Length > 1024) throw new IOException("CLI_COMPATIBILITY_STATE_INVALID");
            var pending = JsonSerializer.Deserialize<ClaudeCompatibilityPending>(File.ReadAllBytes(PendingPath), Options);
            if (pending is null || !ValidPending(pending) || (state.Pending is not null && state.Pending != pending)) throw new IOException("CLI_COMPATIBILITY_STATE_INVALID");
            state.Pending = pending;
        }
        return state;
    }

    public void Write(ClaudeCompatibilityState state)
    {
        Validate(state);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        CheckPrivate(new DirectoryInfo(directory));
        if (File.Exists(StatePath) || new FileInfo(StatePath).LinkTarget is not null) CheckPrivate(new FileInfo(StatePath));
        if (state.Pending is not null)
        {
            if (File.Exists(PendingPath) || new FileInfo(PendingPath).LinkTarget is not null)
            {
                CheckPrivate(new FileInfo(PendingPath));
                if (new FileInfo(PendingPath).Length > 1024 || JsonSerializer.Deserialize<ClaudeCompatibilityPending>(File.ReadAllBytes(PendingPath), Options) != state.Pending)
                    throw new IOException("CLI_PREVIOUS_ATTEMPT_INCOMPLETE");
            }
            else
            {
                using var guard = new FileStream(PendingPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                });
                guard.Write(JsonSerializer.SerializeToUtf8Bytes(state.Pending, Options)); guard.Flush(flushToDisk: true);
                SyncDirectory();
            }
        }
        WriteCheckpoint?.Invoke("before-state-write");
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        // Only released incompatibilities may be compacted. Safety history and migration evidence remain.
        while (bytes.Length > 65536 && state.Blocks.FindIndex(b => b.Contract.Length > 0 && b.ReleasedAt is not null) is int index && index >= 0)
        {
            state.Blocks.RemoveAt(index);
            bytes = JsonSerializer.SerializeToUtf8Bytes(state, Options);
        }
        if (bytes.Length > 65536) throw new IOException("CLI_COMPATIBILITY_STATE_FULL");
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            })) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, StatePath, overwrite: true);
            WriteCheckpoint?.Invoke("after-state-replace");
            SyncDirectory();
            WriteCheckpoint?.Invoke("after-state-sync");
            if (state.Pending is null && File.Exists(PendingPath)) { File.Delete(PendingPath); SyncDirectory(); }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void SyncDirectory()
    {
        int fd = Open(System.Text.Encoding.UTF8.GetBytes(directory + "\0"), 0);
        if (fd < 0) throw new IOException("CLI_COMPATIBILITY_STATE_SYNC_FAILED");
        try { if (Fsync(fd) != 0) throw new IOException("CLI_COMPATIBILITY_STATE_SYNC_FAILED"); }
        finally { _ = Close(fd); }
    }

    public void Release(bool safety, string? binaryId = null)
    {
        // A legacy file is retained as evidence. Only this explicit operator action releases it.
        ClaudeCompatibilityState state;
        if (legacyPath is not null && File.Exists(legacyPath))
        {
            state = new ClaudeCompatibilityStore(directory).Read();
            var legacy = new FileInfo(legacyPath);
            if (legacy.LinkTarget is not null || legacy.Length > 1024) throw new IOException("CLI_LEGACY_MIGRATION_REQUIRED");
            if (!safety) throw new IOException("CLI_LEGACY_MIGRATION_REQUIRED");
            state.LegacyDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyPath)));
            state.LegacyReleased = true;
        }
        else state = Read();
        if (state.Pending is not null && !safety) throw new IOException("CLI_PREVIOUS_ATTEMPT_INCOMPLETE");
        if (safety && state.Pending is { } pending)
        { state.Resolutions.Add(new(pending, DateTimeOffset.UtcNow)); state.Pending = null; }
        state.Blocks = state.Blocks.Select(b => (binaryId is null || b.BinaryId == binaryId) &&
            (safety || b.Contract.Length > 0) && b.ReleasedAt is null ? b with { ReleasedAt = DateTimeOffset.UtcNow } : b).ToList();
        Write(state);
    }

    /// <summary>Operator-only migration using a previously verified version/identity receipt; never infer from a path.</summary>
    public void ImportLegacyMapping(string verifiedVersion, string identityToken, string receiptDigest)
    {
        if (legacyPath is null || !File.Exists(legacyPath) || !IsDigest(receiptDigest)) throw new IOException("CLI_LEGACY_MIGRATION_REQUIRED");
        var legacy = new FileInfo(legacyPath);
        if (legacy.LinkTarget is not null || legacy.Length > 1024 || File.ReadAllText(legacyPath).Trim() != verifiedVersion)
            throw new IOException("CLI_LEGACY_MIGRATION_REQUIRED");
        var identity = MacClaudeBinaryIdentity.Parse(identityToken);
        if (!identity.Token.StartsWith("2|com.anthropic.claude-code|Q6L2SF6YDW|", StringComparison.Ordinal)) throw new IOException("CLI_LEGACY_MIGRATION_REQUIRED");
        var state = new ClaudeCompatibilityStore(directory).Read();
        state.LegacyDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(legacyPath)));
        state.LegacyBinaryId = identity.Key; state.LegacyEvidenceDigest = receiptDigest; state.LegacyReleased = false;
        if (!state.Blocks.Any(b => b.BinaryId == identity.Key && b.Contract.Length == 0 && b.ReleasedAt is null))
            state.Blocks.Add(new(identity.Key, "", "CLI_GROUP_ESCAPE_DETECTED", DateTimeOffset.UtcNow));
        Write(state);
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool IsBinaryId(string? value) => value == "unresolved" || IsDigest(value);
    private static bool ValidPending(ClaudeCompatibilityPending? pending) => pending is null ||
        (pending.AttemptId != Guid.Empty && IsBinaryId(pending.BinaryId) && pending.StartedAt != default);
    private static void Validate(ClaudeCompatibilityState state)
    {
        if (state.SchemaVersion != 1 || state.Blocks is null || state.Resolutions is null || !ValidPending(state.Pending) ||
            (state.LastBinaryId is not null && !IsDigest(state.LastBinaryId)) ||
            (state.LegacyDigest is not null && !IsDigest(state.LegacyDigest)) ||
            (state.LegacyReleased && state.LegacyDigest is null) ||
            (state.LegacyBinaryId is not null && (!IsDigest(state.LegacyBinaryId) || !IsDigest(state.LegacyEvidenceDigest) || state.LegacyDigest is null)) ||
            state.Resolutions.Any(r => r is null || r.Pending is null || !ValidPending(r.Pending) || r.ResolvedAt == default) ||
            state.Blocks.Any(b => b is null || !IsBinaryId(b.BinaryId) || b.Contract is null || b.Contract.Length > 256 ||
                string.IsNullOrEmpty(b.Reason) || b.Reason.Length > 80 || b.Reason.Any(c => !char.IsAsciiLetterUpper(c) && c != '_') || b.DetectedAt == default))
            throw new IOException("CLI_COMPATIBILITY_STATE_INVALID");
    }

    private static void CheckPrivate(FileSystemInfo item)
    {
        if (item.LinkTarget is not null || (item.Attributes & FileAttributes.ReparsePoint) != 0 ||
            ((int)File.GetUnixFileMode(item.FullName) & 0x3f) != 0)
            throw new IOException("CLI_COMPATIBILITY_STATE_PERMISSIONS");
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true)] private static extern int Open(byte[] path, int flags);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "fsync", SetLastError = true)] private static extern int Fsync(int fd);
    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close")] private static extern int Close(int fd);
}
