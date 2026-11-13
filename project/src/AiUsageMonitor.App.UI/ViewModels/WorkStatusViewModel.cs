using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.App.UI.ViewModels;

public sealed class WorkStatusViewModel : INotifyPropertyChanged
{
    public IReadOnlyList<WorkStatusRowViewModel> Rows { get; } =
        [new(WorkProvider.CodexDesktop, "Codex Desktop"), new(WorkProvider.ClaudeCode, "Claude Code")];
    private bool _visible;
    public bool IsVisible { get => _visible; private set { _visible = value; PropertyChanged?.Invoke(this, new(nameof(IsVisible))); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(IWorkStatusView model, bool codex, bool claude, string? problem, DateTimeOffset now,
        IReadOnlySet<WorkProvider>? unavailableProviders = null, IReadOnlyDictionary<string, DateTimeOffset>? claudeBusySince = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        IsVisible = codex || claude;
        foreach (WorkStatusRowViewModel row in Rows)
            row.Update(model, row.Provider == WorkProvider.CodexDesktop ? codex : claude, problem, now,
                problem is null || unavailableProviders is not null && !unavailableProviders.Contains(row.Provider),
                row.Provider == WorkProvider.ClaudeCode ? claudeBusySince : null);
    }
}

public sealed class WorkStatusRowViewModel : INotifyPropertyChanged
{
    internal WorkStatusRowViewModel(WorkProvider provider, string name)
    {
        Provider = provider;
        Name = name;
        AcknowledgeCommand = new AcknowledgeAction(this);
    }
    public WorkProvider Provider { get; }
    public string Name { get; }
    public string Status { get; private set; } = "—";
    public IReadOnlyList<WorkStatusPartViewModel> StatusParts { get; private set; } = [];
    public bool HasStatusParts => StatusParts.Count > 0;
    public string Detail { get; private set; } = "";
    public bool HasDetail => Detail.Length > 0;
    public bool IsVisible { get; private set; }
    public bool HasNotices => NoticeIds.Count > 0;
    public IReadOnlyList<string> NoticeIds { get; private set; } = [];
    public ICommand AcknowledgeCommand { get; }
    public event Action<IReadOnlyList<string>>? AcknowledgeRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Update(IWorkStatusView model, bool visible, string? problem, DateTimeOffset now, bool available,
        IReadOnlyDictionary<string, DateTimeOffset>? busySince)
    {
        IsVisible = visible;
        WorkSession? latest = model.Sessions.Where(s => s.Provider == Provider)
            .OrderByDescending(s => s.At).ThenByDescending(s => s.Order).FirstOrDefault();
        bool received = latest is not null;
        WorkStatusSummary summary = model.Summarize(Provider, now, available && received);
        NoticeIds = summary.Notices.Select(n => n.Id).ToArray();
        var parts = new List<WorkStatusPartViewModel>();
        int running = summary.Running;
        if (visible && available && busySince is not null)
            running += model.Sessions.Count(s => s.Provider == Provider &&
                s.State is WorkSessionState.StopCandidate or WorkSessionState.Completed &&
                busySince.TryGetValue(s.SessionId, out var at) && at > s.At);
        if (running > 0) parts.Add(new($"作業中 {running}", WorkSessionState.Running));
        if (Provider == WorkProvider.CodexDesktop)
        {
            if (summary.QuestionWaiting > 0) parts.Add(new("質問待ち", WorkSessionState.Waiting));
            // A permission check is part of the agent's own turn, not user input, so it uses the running (yellow) indicator.
            if (summary.PermissionWaiting > 0) parts.Add(new("権限確認中", WorkSessionState.Running));
        }
        else if (summary.Waiting > 0) parts.Add(new("入力待ち", WorkSessionState.Waiting));
        if (parts.Count == 0 && available && latest?.State is WorkSessionState.StopCandidate or WorkSessionState.Completed)
            parts.Add(new("応答終了", latest.State));
        StatusParts = parts.ToArray();
        Status = parts.Count > 0 ? string.Join(" / ", parts.Select(part => part.Text)) : "—";
        Detail = problem ?? "";
        foreach (string property in new[] { nameof(IsVisible), nameof(Status), nameof(StatusParts), nameof(HasStatusParts), nameof(Detail), nameof(HasDetail), nameof(HasNotices), nameof(NoticeIds) })
            Changed(property);
        ((AcknowledgeAction)AcknowledgeCommand).Changed();
    }

    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));

    private sealed class AcknowledgeAction(WorkStatusRowViewModel row) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => parameter is IReadOnlyList<string> ids ? ids.Count > 0 : row.HasNotices;
        public void Execute(object? parameter) =>
            row.AcknowledgeRequested?.Invoke(parameter is IReadOnlyList<string> ids ? ids : row.NoticeIds.ToArray());
        public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record WorkStatusPartViewModel(string Text, WorkSessionState State)
{
    public bool IsRunning => State == WorkSessionState.Running;
    public bool IsWaiting => State == WorkSessionState.Waiting;
    public bool IsResponseEnded => State is WorkSessionState.StopCandidate or WorkSessionState.Completed;
}
