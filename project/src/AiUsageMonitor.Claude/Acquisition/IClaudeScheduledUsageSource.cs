using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Acquisition;

/// <summary>A source that inspects candidate changes before applying its own retry policy.</summary>
public interface IClaudeScheduledUsageSource : IClaudeUsageSource
{
    /// <summary>Null means the candidate was inspected and acquisition is deferred until the retry deadline.</summary>
    async Task<ClaudeUsageObservation?> RefreshScheduledAsync(CancellationToken cancellationToken) =>
        await RefreshAsync(cancellationToken).ConfigureAwait(false);

    DateTimeOffset? NextAttemptAt { get; }
    event Action<UsageSnapshot>? ProgressChanged;
    void Cancel();
    void ResetRetry();
    void Recheck(bool releaseSafety);
    string DescribeStop();
}
