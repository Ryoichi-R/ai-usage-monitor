using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>Checks for system sleep and emits one request when the main display is awake.</summary>
public sealed partial class MacPowerResumeMonitor : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly Func<TimeSpan> _readSleepOffset;
    private readonly Func<bool?> _readDisplayAsleep;
    private readonly Action _requestResume;
    private readonly Action<string, Exception?> _diagnostic;
    private TimeSpan? _previousOffset;
    private bool _pending;

    [SupportedOSPlatform("macos")]
    public MacPowerResumeMonitor(Action requestResume, Action<string, Exception?> diagnostic)
        : this(ReadSleepOffset, ReadMainDisplayAsleep, requestResume, diagnostic, TimeSpan.FromSeconds(3)) { }

    internal MacPowerResumeMonitor(Func<TimeSpan> readSleepOffset, Func<bool?> readDisplayAsleep,
        Action requestResume, Action<string, Exception?> diagnostic, TimeSpan interval)
    {
        _readSleepOffset = readSleepOffset;
        _readDisplayAsleep = readDisplayAsleep;
        _requestResume = requestResume;
        _diagnostic = diagnostic;
        _loop = RunAsync(interval, _stop.Token);
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            bool sampleFailed = false;
            do
            {
                try
                {
                    Sample(cancellationToken);
                    sampleFailed = false;
                }
                catch (Exception error)
                {
                    if (!sampleFailed) _diagnostic("power-resume-monitor-failure", error);
                    sampleFailed = true;
                }
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private void Sample(CancellationToken cancellationToken)
    {
        TimeSpan offset = _readSleepOffset();
        bool slept = _previousOffset is { } previous && offset - previous > TimeSpan.FromSeconds(10);
        _previousOffset = offset;
        if (slept)
        {
            _pending = true;
            _diagnostic("power-resume-detected", null);
        }
        if (!_pending || cancellationToken.IsCancellationRequested) return;

        bool? asleep;
        try { asleep = _readDisplayAsleep(); }
        catch (Exception error)
        {
            _diagnostic("power-resume-display-failure", error);
            asleep = null;
        }
        if (asleep == true)
        {
            if (slept) _diagnostic("power-resume-deferred", null);
            return;
        }

        _pending = false;
        if (!cancellationToken.IsCancellationRequested) _requestResume();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }

    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";

    [LibraryImport(SystemLibrary)]
    private static partial ulong mach_absolute_time();

    [LibraryImport(SystemLibrary)]
    private static partial ulong mach_continuous_time();

    [LibraryImport(SystemLibrary)]
    private static partial int mach_timebase_info(out MachTimebase timebase);

    [StructLayout(LayoutKind.Sequential)]
    private struct MachTimebase
    {
        public uint Numerator;
        public uint Denominator;
    }

    private static TimeSpan ReadSleepOffset()
    {
        if (mach_timebase_info(out MachTimebase timebase) != 0 || timebase.Denominator == 0)
            throw new InvalidOperationException("MACH_TIMEBASE_UNAVAILABLE");
        ulong awake = mach_absolute_time();
        ulong continuous = mach_continuous_time();
        double nanoseconds = ((long)continuous - (long)awake) * ((double)timebase.Numerator / timebase.Denominator);
        return TimeSpan.FromSeconds(nanoseconds / 1_000_000_000);
    }

    [SupportedOSPlatform("macos")]
    private static bool? ReadMainDisplayAsleep()
    {
        uint display = ObjCRuntime.CGMainDisplayID();
        return display == 0 || ObjCRuntime.CGDisplayIsOnline(display) == 0
            ? null
            : ObjCRuntime.CGDisplayIsAsleep(display) != 0;
    }
}
