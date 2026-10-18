using AiUsageMonitor.App.UI.Hosting;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class TrayDoubleClickDetectorTests
{
    [Fact]
    public void SecondClickWithinTheSystemIntervalIsADoubleClick()
    {
        var time = new SteppedTime();
        var detector = new TrayDoubleClickDetector(() => TimeSpan.FromMilliseconds(500), time);

        Assert.False(detector.RegisterClick());
        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.True(detector.RegisterClick());

        // ダブルクリックの直後のクリックは新しい1回目として数える。
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.False(detector.RegisterClick());

        // 間隔を超えた2回目は1回目に数え直す。
        time.Advance(TimeSpan.FromMilliseconds(501));
        Assert.False(detector.RegisterClick());
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(detector.RegisterClick());
    }

    [Fact]
    public void TheIntervalIsReadOnEveryClick()
    {
        var time = new SteppedTime();
        TimeSpan interval = TimeSpan.FromMilliseconds(200);
        var detector = new TrayDoubleClickDetector(() => interval, time);
        Assert.False(detector.RegisterClick());
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.False(detector.RegisterClick());
        interval = TimeSpan.FromMilliseconds(400);
        time.Advance(TimeSpan.FromMilliseconds(300));
        Assert.True(detector.RegisterClick());
        Assert.False(new TrayDoubleClickDetector(() => interval).RegisterClick());
    }

    private sealed class SteppedTime : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
