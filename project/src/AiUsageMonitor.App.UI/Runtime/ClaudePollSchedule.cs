namespace AiUsageMonitor.App.UI.Runtime;

/// <summary>Recomputes a sleeping poll when a manual attempt or settings change moves its retry deadline.</summary>
internal sealed class ClaudePollSchedule(Func<DateTimeOffset>? clock = null) : IDisposable
{
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    private int _resumePending;
    private DateTimeOffset? _consumedRetry;

    public void RequestResume()
    {
        Interlocked.Exchange(ref _resumePending, 1);
        Changed();
    }

    public void ConsumeResume() => Interlocked.Exchange(ref _resumePending, 0);

    public void Changed()
    {
        try { _changed.Release(); }
        catch (Exception error) when (error is SemaphoreFullException or ObjectDisposedException) { }
    }

    public async Task<bool> WaitAsync(DateTimeOffset inspectionAt, Func<DateTimeOffset?> nextAttempt, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _resumePending) != 0) return true;
            DateTimeOffset? retry = nextAttempt();
            if (retry is null) _consumedRetry = null;
            DateTimeOffset due = retry is { } attempt && attempt != _consumedRetry && attempt < inspectionAt
                ? attempt : inspectionAt;
            DateTimeOffset now = _clock();
            TimeSpan remaining = due - now;
            if (remaining <= TimeSpan.Zero)
            {
                // A source may fail before replacing its deadline. Dispatch that deadline only once;
                // subsequent polls still inspect on the normal interval or a new retry/resume request.
                if (retry <= now) _consumedRetry = retry;
                return false;
            }
            await _changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _changed.Dispose();
}
