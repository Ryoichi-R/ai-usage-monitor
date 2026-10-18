namespace AiUsageMonitor.App.UI.Runtime;

/// <summary>Recomputes a sleeping poll when a manual attempt or settings change moves its retry deadline.</summary>
internal sealed class ClaudePollSchedule(Func<DateTimeOffset>? clock = null) : IDisposable
{
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public void Changed()
    {
        try { _changed.Release(); }
        catch (Exception error) when (error is SemaphoreFullException or ObjectDisposedException) { }
    }

    public async Task WaitAsync(DateTimeOffset inspectionAt, Func<DateTimeOffset?> nextAttempt, CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset due = nextAttempt() is { } retry && retry < inspectionAt ? retry : inspectionAt;
            TimeSpan remaining = due - _clock();
            if (remaining <= TimeSpan.Zero) return;
            if (!await _changed.WaitAsync(remaining, cancellationToken).ConfigureAwait(false)) return;
        }
    }

    public void Dispose() => _changed.Dispose();
}
