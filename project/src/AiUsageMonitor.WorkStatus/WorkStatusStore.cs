using System.Text.Json;
using System.Text.Json.Serialization;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus;

public sealed class WorkStatusStore(string root)
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
    public string Root { get; } = Path.GetFullPath(root);

    public WorkStatusModel Load()
    {
        string path = Path.Combine(Root, "state.json");
        byte[] bytes;
        try
        {
            using var stream = OpenSnapshot(path);
            if (stream.Length > MaxBytes) throw new IOException("WORK_STATUS_STATE_TOO_LARGE");
            bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        WorkStatusModel? value = JsonSerializer.Deserialize<WorkStatusModel>(bytes, JsonOptions);
        Validate(value);
        return value!;
    }

    public void Save(WorkStatusModel value)
    {
        Validate(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaxBytes) throw new IOException("WORK_STATUS_STATE_TOO_LARGE");
        string path = WorkStatusPaths.Inside(Root, Path.Combine(Root, "state.json"));
        Directory.CreateDirectory(Root);
        string temporary = WorkStatusPaths.Inside(Root, path + ".new-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            WorkStatusPaths.RejectLinks(path);
            // MoveFileEx overwrite rejects an open destination even when it shares DELETE.
            // ReplaceFile preserves the snapshot while readers retain their original handle.
            if (OperatingSystem.IsWindows() && File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary) && File.Exists(path)) File.Delete(temporary);
        }
    }

    private FileStream OpenSnapshot(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(WorkStatusPaths.Inside(Root, path), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
            // ReplaceFile briefly owns the replacement handle exclusively. Retry only
            // Windows missing-file/sharing/lock races; permissions and corrupt state still fail closed.
            catch (IOException error) when (OperatingSystem.IsWindows() && attempt < 5 &&
                (error.HResult & 0xffff) is 2 or 32 or 33)
            { Thread.Sleep(10 * (attempt + 1)); }
        }
    }
    public static WorkStatusModel Copy(WorkStatusModel value) =>
        JsonSerializer.Deserialize<WorkStatusModel>(JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), JsonOptions)!;

    private static void Validate(WorkStatusModel? value)
    {
        if (value is null || value.Version != WorkStatusModel.CurrentVersion || value.Sessions is null ||
            value.Notices is null || value.ProcessedIds is null || value.AcknowledgedIds is null || value.Imports is null ||
            value.PendingQuestions is null || value.AnsweredQuestionIds is null ||
            value.PendingQuestions.Any(q => q is null || q.SessionId is not { Length: > 0 and <= 160 } ||
                q.Id is not { Length: 24 } || !q.Id.All(char.IsAsciiHexDigit)) ||
            value.AnsweredQuestionIds.Any(id => id is not { Length: > 0 and <= 185 }) ||
            value.Sessions.Any(s => s is null || !Enum.IsDefined(s.Provider) || !Enum.IsDefined(s.State) ||
                s.SessionId is not { Length: > 0 and <= 160 } || s.TurnId is not { Length: <= 160 } ||
                s.InputPending && (s.Provider != WorkProvider.ClaudeCode || s.State != WorkSessionState.Waiting)) ||
            value.Notices.Any(n => n is null || !Enum.IsDefined(n.Provider) || !Enum.IsDefined(n.State) ||
                n.Id is not { Length: > 0 and <= 500 } || n.SessionId is not { Length: > 0 and <= 160 } || n.TurnId is not { Length: <= 160 }) ||
            value.ProcessedIds.Any(id => id is not { Length: > 0 and <= 240 }) ||
            value.AcknowledgedIds.Any(id => id is not { Length: > 0 and <= 500 }) ||
            value.Imports.Any(id => id is not { Length: > 0 and <= 240 }))
            throw new IOException("WORK_STATUS_STATE_SCHEMA_INVALID");
    }
}
