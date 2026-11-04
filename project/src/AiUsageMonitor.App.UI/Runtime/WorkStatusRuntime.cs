using AiUsageMonitor.Core.WorkStatus;
using AiUsageMonitor.WorkStatus;

namespace AiUsageMonitor.App.UI.Runtime;

internal sealed class WorkStatusRuntime : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Action<string, Exception?> _diagnostic;
    private readonly WorkStatusCollector? _collector;
    private readonly string? _startupProblem;
    private readonly string _root;
    private WorkStatusSnapshot _current = new(new(), null);

    public WorkStatusRuntime(string root, Action<string, Exception?> diagnostic)
    {
        _root = root;
        _diagnostic = diagnostic;
        try { _collector = new(new(root)); _current = _collector.Current; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _startupProblem = "作業状態を読み込めません。元の状態ファイルは保持しています。";
            _current = new(new(), _startupProblem);
            Report("work-status-load-failed", error);
        }
    }

    public WorkStatusSnapshot Current => _current;

    public async Task<WorkStatusSnapshot> PollAsync(CancellationToken cancellationToken)
    {
        if (_collector is null) return new(_current.Model, _startupProblem);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _current = await Task.Run(() => _collector.Poll(DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            if (_current.Problem is { } reason)
            {
                _diagnostic(reason, null);
                _current = _current with { Problem = "一部の作業記録を取得できません。未確認通知は保持しています。" };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _current = new(_current.Model, "作業状態の取得・保存に失敗。未確認通知は保持しています。");
            Report("work-status-poll-failed", error);
        }
        finally { _gate.Release(); }
        return _current;
    }

    public async Task<WorkStatusSnapshot> AcknowledgeAsync(IReadOnlyList<string> ids)
    {
        if (_collector is null) return _current;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _current = await Task.Run(() => _collector.Acknowledge(ids)).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _current = new(_current.Model, "確認状態を保存できません。未確認通知を残しています。");
            Report("work-status-acknowledge-failed", error);
        }
        finally { _gate.Release(); }
        return _current;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _gate.Dispose();
    }

    public async Task<bool> ImportLegacyAsync(string oldRoot)
    {
        if (_collector is null) return false;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _current = await Task.Run(() => _collector.ImportLegacy(oldRoot)).ConfigureAwait(false); return true; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or FormatException or KeyNotFoundException or OverflowException)
        { Report("work-status-import-failed", error); return false; }
        finally { _gate.Release(); }
    }

    private void Report(string reason, Exception error)
    {
        _diagnostic(reason, error);
        try { WorkStatusErrorLog.Write(_root, error); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { _diagnostic("work-status-error-log-failed", failure); }
    }
}
