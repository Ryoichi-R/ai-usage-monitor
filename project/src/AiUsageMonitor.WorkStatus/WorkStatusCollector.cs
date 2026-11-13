using System.Collections.Frozen;
using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus;

public sealed record WorkStatusSnapshot(WorkStatusModel Model, string? Problem, IReadOnlySet<WorkProvider>? UnavailableProviders = null);

public sealed record WorkStatusViewSnapshot(WorkStatusView Model, string? Problem, IReadOnlySet<WorkProvider>? UnavailableProviders = null)
{
    public static WorkStatusViewSnapshot Empty { get; } = new(new(new()), null);
}

public sealed class WorkStatusView : IWorkStatusView
{
    internal WorkStatusView(WorkStatusModel model)
    {
        Model = model;
        Sessions = model.Sessions.AsReadOnly();
        Notices = model.Notices.AsReadOnly();
    }

    // The collector owns this model and only edits private copies after publication.
    internal WorkStatusModel Model { get; }
    public IReadOnlyList<WorkSession> Sessions { get; }
    public IReadOnlyList<WorkNotice> Notices { get; }
    public WorkStatusSummary Summarize(WorkProvider provider, DateTimeOffset now, bool available) =>
        Model.Summarize(provider, now, available);
}

public sealed class WorkStatusCollector
{
    private readonly WorkStatusStore _store;
    private WorkStatusView _view;
    private string? _problem;
    private readonly HashSet<string> _invalidFiles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryAfter = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkProvider> _invalidProviders = new(StringComparer.Ordinal);

    public WorkStatusCollector(WorkStatusStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _view = new(store.Load());
    }

    public WorkStatusSnapshot Current => CopySnapshot(CurrentView);
    public WorkStatusViewSnapshot CurrentView => new(_view, _problem,
        // An unreadable/unattributed event may belong to either provider. Do not present stale
        // sessions as healthy, including after acknowledgement or legacy import.
        _retryAfter.Count > 0 || _invalidFiles.Any(path => !_invalidProviders.ContainsKey(path))
            ? null
            : _invalidProviders.Where(pair => _invalidFiles.Contains(pair.Key)).Select(pair => pair.Value).ToFrozenSet());

    public WorkStatusSnapshot Poll(DateTimeOffset now) => CopySnapshot(PollView(now));

    public WorkStatusViewSnapshot PollView(DateTimeOffset now)
    {
        string events = WorkStatusPaths.Inside(_store.Root, Path.Combine(_store.Root, "events"));
        HashSet<string> paths = Directory.Exists(events)
            ? Directory.EnumerateFiles(events, "*.json").ToHashSet(StringComparer.Ordinal)
            : new(StringComparer.Ordinal);
        WorkStatusModel? candidate = null;
        var processed = new List<(string Path, string Id)>();
        _invalidFiles.RemoveWhere(path => !paths.Contains(path));
        foreach (string path in _invalidProviders.Keys.Where(path => !_invalidFiles.Contains(path)).ToArray())
            _invalidProviders.Remove(path);
        foreach (string path in _retryAfter.Keys.Where(path => !paths.Contains(path)).ToArray())
            _retryAfter.Remove(path);
        string? problem = _invalidFiles.Count > 0 ? "WORK_STATUS_EVENT_INVALID" : null;
        foreach (string path in paths.Order(StringComparer.Ordinal)
            .Where(path => !_invalidFiles.Contains(path) && (!_retryAfter.TryGetValue(path, out var retry) || now >= retry)).Take(512))
        {
            try
            {
                WorkStatusPaths.Inside(events, path);
                if (new FileInfo(path).Length > 4096)
                {
                    _retryAfter.Remove(path);
                    _invalidFiles.Add(path);
                    problem = "WORK_STATUS_EVENT_TOO_LARGE";
                    continue;
                }
                WorkStatusEvent? value = JsonSerializer.Deserialize<WorkStatusEvent>(File.ReadAllBytes(path), WorkStatusStore.JsonOptions);
                _retryAfter.Remove(path);
                if (!WorkStatusModel.Valid(value) || value!.At > now.AddMinutes(1))
                {
                    _invalidFiles.Add(path);
                    if (value is not null && Enum.IsDefined(value.Provider)) _invalidProviders[path] = value.Provider;
                    problem = "WORK_STATUS_EVENT_INVALID";
                    continue;
                }
                (candidate ??= WorkStatusStore.Copy(_view.Model)).Apply(value);
                processed.Add((path, value.Id));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { _retryAfter[path] = now.AddSeconds(30); problem = "WORK_STATUS_EVENT_UNREADABLE"; }
        }
        // Commit state and deduplication before deleting any processed spool file.
        if (candidate is not null)
        {
            _store.Save(candidate);
            foreach (var item in processed)
            {
                try
                {
                    WorkStatusPaths.Inside(events, item.Path);
                    File.Delete(item.Path);
                    // Session ordering plus acknowledged notice IDs prevents resurrection.
                    // The last durable batch still contains these IDs if the app crashes here.
                    candidate.ProcessedIds.Remove(item.Id);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                { problem = "WORK_STATUS_SPOOL_CLEANUP_FAILED"; }
            }
            _view = new(candidate);
        }
        // Backoff suppresses repeated reads, not the warning. Clear it only after a successful
        // read or after the file disappears from the spool.
        _problem = problem ?? (_retryAfter.Count > 0 ? "WORK_STATUS_EVENT_UNREADABLE" : null);
        return CurrentView;
    }

    public WorkStatusSnapshot Acknowledge(IReadOnlyList<string> ids) => CopySnapshot(AcknowledgeView(ids));

    public WorkStatusViewSnapshot AcknowledgeView(IReadOnlyList<string> ids)
    {
        WorkStatusModel candidate = WorkStatusStore.Copy(_view.Model);
        candidate.Acknowledge(ids);
        _store.Save(candidate);
        _view = new(candidate);
        return CurrentView;
    }

    public WorkStatusSnapshot ImportLegacy(string oldRoot) => CopySnapshot(ImportLegacyView(oldRoot));

    public WorkStatusViewSnapshot ImportLegacyView(string oldRoot)
    {
        string backup = WorkStatusPaths.Inside(_store.Root, Path.Combine(_store.Root, "legacy-backups"));
        WorkStatusModel candidate = LegacyStateImporter.Read(oldRoot, _view.Model, backup);
        _store.Save(candidate);
        _view = new(candidate);
        return CurrentView;
    }

    private static WorkStatusSnapshot CopySnapshot(WorkStatusViewSnapshot snapshot) =>
        new(WorkStatusStore.Copy(snapshot.Model.Model), snapshot.Problem, snapshot.UnavailableProviders);

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
