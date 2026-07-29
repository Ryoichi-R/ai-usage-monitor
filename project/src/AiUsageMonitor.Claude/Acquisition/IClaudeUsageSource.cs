namespace AiUsageMonitor.Claude.Acquisition;

/// <summary>Claude利用率を能動取得するsource。</summary>
public interface IClaudeUsageSource
{
    Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken);
}
