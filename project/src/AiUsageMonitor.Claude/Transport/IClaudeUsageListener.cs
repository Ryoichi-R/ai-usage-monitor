using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.Transport;

/// <summary>
/// 常駐Claude CodeセッションのstatusLineから届くpassive観測の受信口。
/// WindowsはNamed Pipe（<see cref="ClaudeUsagePipeServer"/>）、macOSはUnixドメインソケットで実装する。
/// 受信内容は<see cref="StatusLine.ClaudeStatusLineParser"/>で解析済みのsnapshotとして通知する。
/// </summary>
public interface IClaudeUsageListener : IAsyncDisposable
{
    event Action<UsageSnapshot>? ObservationReceived;

    void Start();
}
