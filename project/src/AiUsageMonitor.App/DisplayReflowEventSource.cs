using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace AiUsageMonitor.App;

[EventSource(Name = "AiUsageMonitor.DisplayReflow")]
internal sealed class DisplayReflowEventSource : EventSource
{
    internal static DisplayReflowEventSource Log { get; } = new();

    private DisplayReflowEventSource() { }

    [Event(1, Level = EventLevel.Informational)]
    internal void WaveStarted(long waveId, long timestamp, bool retry)
    {
        if (IsEnabled()) WriteEvent(1, waveId, timestamp, retry);
    }

    [Event(2, Level = EventLevel.Informational)]
    internal void Notification(long waveId, long timestamp, bool retry, string message)
    {
        if (IsEnabled()) WriteEvent(2, waveId, timestamp, retry, message);
    }

    [Event(3, Level = EventLevel.Informational)]
    internal void TimerFired(long waveId, long timestamp, bool retry, string timerName)
    {
        if (IsEnabled()) WriteEvent(3, waveId, timestamp, retry, timerName);
    }

    [Event(4, Level = EventLevel.Informational)]
    internal void Placement(long waveId, long timestamp, bool retry, string stage)
    {
        if (IsEnabled()) WriteEvent(4, waveId, timestamp, retry, stage);
    }

    [Event(5, Level = EventLevel.Warning)]
    internal void InteropFailure(long waveId, long timestamp, bool retry, string stage, int hresult)
    {
        if (IsEnabled()) WriteEvent(5, waveId, timestamp, retry, stage, hresult);
    }

    [Event(6, Level = EventLevel.Informational)]
    internal void PlacementConfirmed(
        long waveId,
        long timestamp,
        bool retry,
        string deviceName,
        int left,
        int top,
        int width,
        int height)
    {
        if (IsEnabled()) WriteEvent(6, waveId, timestamp, retry, deviceName, left, top, width, height);
    }

    [Event(7, Level = EventLevel.Warning)]
    internal void BackgroundFallback(
        long waveId,
        long timestamp,
        string deviceName,
        int left,
        int top,
        int width,
        int height)
    {
        if (IsEnabled()) WriteEvent(7, waveId, timestamp, deviceName, left, top, width, height);
    }

    // 既存の配置完了呼び出しとの互換用。設定値やfractionは出力しない。
    internal void Placement(string stage) =>
        Placement(0, Stopwatch.GetTimestamp(), false, stage);

    internal void Failure(string stage) =>
        InteropFailure(0, Stopwatch.GetTimestamp(), false, stage, 0);

    internal void Notification(string kind) =>
        Notification(0, Stopwatch.GetTimestamp(), false, kind);

    internal void PlacementConfirmed(string deviceName, int left, int top, int width, int height) =>
        PlacementConfirmed(0, Stopwatch.GetTimestamp(), false, deviceName, left, top, width, height);

}
