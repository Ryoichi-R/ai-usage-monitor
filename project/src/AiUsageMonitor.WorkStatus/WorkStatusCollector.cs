using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus;

public sealed record WorkStatusSnapshot(WorkStatusModel Model, string? Problem, IReadOnlySet<WorkProvider>? UnavailableProviders = null);

public sealed class WorkStatusCollector
{
    private readonly WorkStatusStore _store;
    private WorkStatusModel _model;
    private readonly HashSet<string> _invalidFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkProvider> _invalidProviders = new(StringComparer.Ordinal);

    public WorkStatusCollector(WorkStatusStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _model = store.Load();
    }

    public WorkStatusSnapshot Current => new(WorkStatusStore.Copy(_model), null);

    public WorkStatusSnapshot Poll(DateTimeOffset now)
    {
        string events = WorkStatusPaths.Inside(_store.Root, Path.Combine(_store.Root, "events"));
        if (!Directory.Exists(events)) return new(WorkStatusStore.Copy(_model), null);
        WorkStatusModel candidate = WorkStatusStore.Copy(_model);
        var processed = new List<(string Path, string Id)>();
        _invalidFiles.RemoveWhere(path => !File.Exists(path));
        string? problem = _invalidFiles.Count > 0 ? "WORK_STATUS_EVENT_INVALID" : null;
        foreach (string path in Directory.EnumerateFiles(events, "*.json").Order(StringComparer.Ordinal)
            .Where(path => !_invalidFiles.Contains(path) && (!_retryAfter.TryGetValue(path, out var retry) || now >= retry)).Take(512))
        {
            try
            {
                WorkStatusPaths.Inside(events, path);
                if (new FileInfo(path).Length > 4096) { _invalidFiles.Add(path); problem = "WORK_STATUS_EVENT_TOO_LARGE"; continue; }
                WorkStatusEvent? value = JsonSerializer.Deserialize<WorkStatusEvent>(File.ReadAllBytes(path), WorkStatusStore.JsonOptions);
                if (!WorkStatusModel.Valid(value) || value!.At > now.AddMinutes(1))
                {
                    _invalidFiles.Add(path);
                    if (value is not null && Enum.IsDefined(value.Provider)) _invalidProviders[path] = value.Provider;
                    problem = "WORK_STATUS_EVENT_INVALID";
                    continue;
                }
                candidate.Apply(value);
                _retryAfter.Remove(path);
                processed.Add((path, value.Id));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { _retryAfter[path] = now.AddSeconds(30); problem = "WORK_STATUS_EVENT_UNREADABLE"; }
        }
        // Commit state and deduplication before deleting any processed spool file.
        if (processed.Count > 0)
        {
            _store.Save(candidate);
            _model = candidate;
            foreach (var item in processed)
            {
                try
                {
                    WorkStatusPaths.Inside(events, item.Path);
                    File.Delete(item.Path);
                    // Session ordering plus acknowledged notice IDs prevents resurrection.
                    // The last durable batch still contains these IDs if the app crashes here.
                    _model.ProcessedIds.Remove(item.Id);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { problem = "WORK_STATUS_SPOOL_CLEANUP_FAILED"; }
            }
        }
        return new(WorkStatusStore.Copy(_model), problem,
            _invalidProviders.Where(pair => _invalidFiles.Contains(pair.Key)).Select(pair => pair.Value).ToHashSet());
    }

    public WorkStatusSnapshot Acknowledge(IReadOnlyList<string> ids)
    {
        WorkStatusModel candidate = WorkStatusStore.Copy(_model);
        candidate.Acknowledge(ids);
        _store.Save(candidate);
        _model = candidate;
        return Current;
    }

    public WorkStatusSnapshot ImportLegacy(string oldRoot)
    {
        string backup = WorkStatusPaths.Inside(_store.Root, Path.Combine(_store.Root, "legacy-backups"));
        WorkStatusModel candidate = LegacyStateImporter.Read(oldRoot, _model, backup);
        _store.Save(candidate);
        _model = candidate;
        return Current;
    }

    public static void WriteEvent(string root, WorkStatusEvent value)
    {
        if (!WorkStatusModel.Valid(value)) throw new ArgumentException("WORK_STATUS_EVENT_INVALID", nameof(value));
        string events = WorkStatusPaths.Inside(root, Path.Combine(root, "events"));
        Directory.CreateDirectory(events);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, WorkStatusStore.JsonOptions);
        if (bytes.Length > 4096) throw new IOException("WORK_STATUS_EVENT_TOO_LARGE");
        string path = WorkStatusPaths.Inside(events, Path.Combine(events, $"{value.At.UtcTicks:D19}-{value.Id}.json"));
        string temporary = path + ".new-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { stream.Write(bytes); stream.Flush(true); }
        File.Move(temporary, path);
    }
}
