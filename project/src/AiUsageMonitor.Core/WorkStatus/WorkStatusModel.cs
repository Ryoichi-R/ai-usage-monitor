using System.Text.Json.Serialization;

namespace AiUsageMonitor.Core.WorkStatus;

public enum WorkProvider { CodexDesktop = 0, ClaudeCode = 3 }
public enum WorkEventKind { Connected, Started, Activity, Waiting, StopCandidate, Completed, Interrupted, Failed, Ended, QuestionOpened, QuestionAnswered }
public enum WorkSessionState { Unknown, Idle, Running, Waiting, StopCandidate, Completed, Interrupted, Failed }

public sealed record WorkStatusEvent([property: JsonRequired] int Protocol, [property: JsonRequired] string Id,
    [property: JsonRequired] WorkProvider Provider, [property: JsonRequired] string SessionId,
    [property: JsonRequired] string TurnId, [property: JsonRequired] WorkEventKind Kind,
    [property: JsonRequired] DateTimeOffset At, [property: JsonRequired] bool DesktopVerified,
    long Order = 0, bool CompletionRetractable = false, string[]? QuestionIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool? InputPending = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool OrdinaryPrompt = false);
public sealed record WorkQuestion(string SessionId, string Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DateTimeOffset? OpenedAt = null);
public sealed record WorkSession(WorkProvider Provider, string SessionId, string TurnId,
    WorkSessionState State, DateTimeOffset At, long Order, bool CompletionRetractable = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool InputPending = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DateTimeOffset? LastOrdinaryPromptAt = null);
public sealed record WorkNotice(string Id, WorkProvider Provider, string SessionId, string TurnId,
    WorkSessionState State, DateTimeOffset At);
public sealed record WorkStatusSummary(int Running, int Waiting, int Unknown, int Completed,
    int Candidates, int Attention, IReadOnlyList<WorkNotice> Notices)
{
    public int Interrupted => Notices.Count(n => n.State == WorkSessionState.Interrupted);
    public int Failed => Notices.Count(n => n.State == WorkSessionState.Failed);
}

public sealed class WorkStatusModel
{
    public const int CurrentVersion = 1;
    [JsonRequired] public int Version { get; set; } = CurrentVersion;
    [JsonRequired] public List<WorkSession> Sessions { get; set; } = [];
    [JsonRequired] public List<WorkNotice> Notices { get; set; } = [];
    [JsonRequired] public HashSet<string> ProcessedIds { get; set; } = [];
    [JsonRequired] public HashSet<string> AcknowledgedIds { get; set; } = [];
    [JsonRequired] public HashSet<string> Imports { get; set; } = [];
    public List<WorkQuestion> PendingQuestions { get; set; } = [];
    public HashSet<string> AnsweredQuestionIds { get; set; } = [];

    public static bool Valid(WorkStatusEvent? value) => value is { Protocol: 1, DesktopVerified: true } &&
        Enum.IsDefined(value.Provider) && Enum.IsDefined(value.Kind) &&
        value.Id is { Length: > 0 and <= 240 } && value.SessionId is { Length: > 0 and <= 160 } &&
        value.TurnId is { Length: <= 160 } && value.At.Offset == TimeSpan.Zero &&
        (value.InputPending is null || value.Provider == WorkProvider.ClaudeCode &&
            (value.InputPending.Value ? value.Kind == WorkEventKind.Waiting : value.Kind == WorkEventKind.Activity)) &&
        (!value.OrdinaryPrompt || value is { Provider: WorkProvider.CodexDesktop, Kind: WorkEventKind.Started, QuestionIds: null }) &&
        (value.QuestionIds is null || value.Provider == WorkProvider.CodexDesktop &&
            value.Kind is WorkEventKind.Started or WorkEventKind.QuestionOpened or WorkEventKind.QuestionAnswered && value.QuestionIds.Length <= 16 &&
            value.QuestionIds.All(id => id is { Length: 24 } && id.All(char.IsAsciiHexDigit))) &&
        (value.Kind is not (WorkEventKind.QuestionOpened or WorkEventKind.QuestionAnswered) || value.QuestionIds is { Length: > 0 });

    public bool Apply(WorkStatusEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Valid(value) || !ProcessedIds.Add(value.Id)) return false;
        int index = Sessions.FindIndex(s => s.Provider == value.Provider && s.SessionId == value.SessionId);
        WorkSession? previous = index >= 0 ? Sessions[index] : null;
        DateTimeOffset? lastOrdinaryPromptAt = previous?.LastOrdinaryPromptAt;
        if (value.OrdinaryPrompt && (lastOrdinaryPromptAt is null || value.At > lastOrdinaryPromptAt))
        {
            lastOrdinaryPromptAt = value.At;
            RetireQuestions(value.SessionId, value.At);
        }
        if (value.QuestionIds is not null)
        {
            foreach (string id in value.QuestionIds)
            {
                string key = value.SessionId + ":" + id;
                if (value.Kind == WorkEventKind.QuestionOpened)
                {
                    if (value.At < lastOrdinaryPromptAt) AnsweredQuestionIds.Add(key);
                    else if (!AnsweredQuestionIds.Contains(key) && !PendingQuestions.Any(q => q.SessionId == value.SessionId && q.Id == id))
                        PendingQuestions.Add(new(value.SessionId, id, value.At));
                }
                else
                {
                    AnsweredQuestionIds.Add(key);
                    PendingQuestions.RemoveAll(q => q.SessionId == value.SessionId && q.Id == id);
                }
            }
        }
        if (value.Kind is WorkEventKind.QuestionOpened or WorkEventKind.QuestionAnswered)
        {
            if (previous is null && value.Kind == WorkEventKind.QuestionOpened)
                Sessions.Add(new(value.Provider, value.SessionId, value.TurnId, WorkSessionState.Running, value.At, value.Order));
            return true;
        }
        if (previous is not null && (value.At < previous.At ||
            value.At == previous.At && value.Order <= previous.Order))
        {
            // A later activity can arrive before its prompt. Retain the question boundary
            // without rolling back the newer session state.
            if (lastOrdinaryPromptAt != previous.LastOrdinaryPromptAt)
                Sessions[index] = previous with { LastOrdinaryPromptAt = lastOrdinaryPromptAt };
            return true;
        }
        if (previous is not null && value.Kind != WorkEventKind.Started && value.TurnId.Length > 0 &&
            previous.TurnId.Length > 0 && value.TurnId != previous.TurnId) return true;
        // Uncorrelated Claude terminal hooks cannot safely close a newer generation.
        string turn = value.TurnId.Length > 0 ? value.TurnId :
            value.Kind == WorkEventKind.Started ? value.Id : previous?.TurnId ?? value.Id;
        WorkSessionState state = value.Kind switch
        {
            WorkEventKind.Connected => WorkSessionState.Idle,
            WorkEventKind.Started => WorkSessionState.Running,
            WorkEventKind.Activity => previous?.State is WorkSessionState.Waiting or WorkSessionState.StopCandidate ||
                previous?.CompletionRetractable == true ? WorkSessionState.Running : previous?.State ?? WorkSessionState.Unknown,
            WorkEventKind.Waiting => WorkSessionState.Waiting,
            WorkEventKind.StopCandidate => WorkSessionState.StopCandidate,
            WorkEventKind.Completed => WorkSessionState.Completed,
            WorkEventKind.Interrupted => WorkSessionState.Interrupted,
            WorkEventKind.Failed => WorkSessionState.Failed,
            WorkEventKind.Ended => WorkSessionState.Idle,
            _ => WorkSessionState.Unknown,
        };
        // Only the exact question outcome clears an explicit Claude input wait.
        // Unrelated tool activity or repeated permission notifications are not an answer.
        bool inputPending = value.Kind is WorkEventKind.Waiting or WorkEventKind.Activity &&
            (value.InputPending ?? previous?.InputPending ?? false);
        if (inputPending) state = WorkSessionState.Waiting;
        if (value.Kind == WorkEventKind.Ended && value.Provider == WorkProvider.CodexDesktop)
            RetireQuestions(value.SessionId);
        if (value.Kind is WorkEventKind.Started or WorkEventKind.Activity or WorkEventKind.Completed)
            Notices.RemoveAll(n => n.Provider == value.Provider && n.SessionId == value.SessionId &&
                (n.State == WorkSessionState.StopCandidate || value.Kind == WorkEventKind.Activity &&
                    previous?.CompletionRetractable == true && n.TurnId == turn && n.State == WorkSessionState.Completed));
        var session = new WorkSession(value.Provider, value.SessionId, turn, state, value.At,
            value.Order, value.Kind == WorkEventKind.Completed && value.CompletionRetractable, inputPending, lastOrdinaryPromptAt);
        if (index >= 0) Sessions[index] = session; else Sessions.Add(session);
        if (state is WorkSessionState.Completed or WorkSessionState.Interrupted or WorkSessionState.Failed or WorkSessionState.StopCandidate)
        {
            string id = $"{value.Provider}:{value.SessionId}:{turn}:{state}";
            if (!AcknowledgedIds.Contains(id) && !Notices.Any(n => n.Id == id))
                Notices.Add(new(id, value.Provider, value.SessionId, turn, state, value.At));
        }
        return true;
    }

    private void RetireQuestions(string sessionId, DateTimeOffset? before = null)
    {
        PendingQuestions.RemoveAll(question =>
        {
            if (question.SessionId != sessionId || (before is not null && question.OpenedAt >= before)) return false;
            AnsweredQuestionIds.Add(sessionId + ":" + question.Id);
            return true;
        });
    }

    public void Acknowledge(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        foreach (WorkNotice notice in Notices.Where(n => selected.Contains(n.Id))) AcknowledgedIds.Add(notice.Id);
        foreach (WorkSession session in Sessions.Where(s => s.State is WorkSessionState.Unknown or WorkSessionState.Running or WorkSessionState.Waiting))
            if (selected.Contains(CheckId(session))) AcknowledgedIds.Add(CheckId(session));
        Notices.RemoveAll(n => selected.Contains(n.Id));
    }

    private static string CheckId(WorkSession session) =>
        $"status:{session.Provider}:{session.SessionId}:{session.TurnId}:{session.At.UtcTicks}:{session.Order}";

    public WorkStatusSummary Summarize(WorkProvider provider, DateTimeOffset now, bool available)
    {
        WorkSession[] sessions = [.. Sessions.Where(s => s.Provider == provider)];
        bool Fresh(WorkSession session) => available &&
            (session.InputPending || now - session.At < TimeSpan.FromMinutes(10));
        bool QuestionPending(WorkSession session) => provider == WorkProvider.CodexDesktop &&
            PendingQuestions.Any(q => q.SessionId == session.SessionId);
        WorkNotice[] notices = [.. Notices.Where(n => n.Provider == provider)];
        WorkNotice[] checks = [.. sessions.Where(s => !QuestionPending(s) && (s.State == WorkSessionState.Unknown || !Fresh(s) &&
                s.State is WorkSessionState.Running or WorkSessionState.Waiting) && !AcknowledgedIds.Contains(CheckId(s)))
            .Select(s => new WorkNotice(CheckId(s), s.Provider, s.SessionId, s.TurnId, WorkSessionState.Unknown, s.At))];
        return new(sessions.Count(s => !QuestionPending(s) && Fresh(s) && s.State == WorkSessionState.Running),
            sessions.Count(s => available && QuestionPending(s) || !QuestionPending(s) && Fresh(s) && s.State == WorkSessionState.Waiting),
            checks.Length,
            notices.Count(n => n.State == WorkSessionState.Completed),
            notices.Count(n => n.State == WorkSessionState.StopCandidate),
            notices.Count(n => n.State is WorkSessionState.Failed or WorkSessionState.Interrupted), [.. notices, .. checks]);
    }
}
