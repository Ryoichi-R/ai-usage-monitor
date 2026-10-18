using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Acquisition;

/// <summary>A source that inspects candidate changes before applying its own retry policy.</summary>
public interface IClaudeScheduledUsageSource : IClaudeUsageSource
{
    DateTimeOffset? NextAttemptAt { get; }
    event Action<UsageSnapshot>? ProgressChanged;
    void Cancel();
    void ResetRetry();
    void Recheck(bool releaseSafety);
    string DescribeStop();
}
