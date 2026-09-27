using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.App.UI.Hosting;

/// <summary>
/// active取得（監視アプリがClaude CLIを起動して/usageを読む経路）をまだ提供しないOS向けのsource。
/// CLIを起動せず、常に<see cref="UsageAvailability.Unsupported"/>を返す。passive取得は別経路で継続する。
/// </summary>
public sealed class UnavailableClaudeUsageSource : IClaudeUsageSource
{
    public const string DefaultReason = "UNSUPPORTED_PLATFORM";

    private readonly string _reason;
    private readonly TimeProvider _time;

    public UnavailableClaudeUsageSource(string reason = DefaultReason, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        _reason = reason;
        _time = time ?? TimeProvider.System;
    }

    public Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DateTimeOffset now = _time.GetUtcNow();
        UsageSnapshot snapshot = UsageSnapshot.Loading(UsageProvider.Claude, now) with
        {
            Availability = UsageAvailability.Unsupported,
            Reason = _reason,
        };
        return Task.FromResult(new ClaudeUsageObservation(ClaudeUsageSourceKind.CliScreen, snapshot));
    }
}
