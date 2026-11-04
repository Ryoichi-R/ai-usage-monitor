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
        string path = WorkStatusPaths.Inside(Root, Path.Combine(Root, "state.json"));
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
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
                s.SessionId is not { Length: > 0 and <= 160 } || s.TurnId is not { Length: <= 160 }) ||
            value.Notices.Any(n => n is null || !Enum.IsDefined(n.Provider) || !Enum.IsDefined(n.State) ||
                n.Id is not { Length: > 0 and <= 500 } || n.SessionId is not { Length: > 0 and <= 160 } || n.TurnId is not { Length: <= 160 }) ||
            value.ProcessedIds.Any(id => id is not { Length: > 0 and <= 240 }) ||
            value.AcknowledgedIds.Any(id => id is not { Length: > 0 and <= 500 }) ||
            value.Imports.Any(id => id is not { Length: > 0 and <= 240 }))
            throw new IOException("WORK_STATUS_STATE_SCHEMA_INVALID");
    }
}
