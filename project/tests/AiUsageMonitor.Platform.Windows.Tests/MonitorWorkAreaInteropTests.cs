using AiUsageMonitor.Platform.Windows.Window;

namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class MonitorWorkAreaInteropTests
{
    [Fact]
    [Trait("Category", "Interactive")]
    public void NativeResolverReturnsPositiveScaleAndWorkArea()
    {
        MonitorResolutionResult result = new MonitorWorkAreaResolver().Resolve(
            savedDeviceName: null,
            windowHandle: 0,
            centerPointPixels: (0, 0));

        Assert.True(result.Succeeded, result.FailureStage);
        Assert.True(result.Snapshot.WorkingArea.Width > 0);
        Assert.True(result.Snapshot.WorkingArea.Height > 0);
        Assert.True(result.Snapshot.DpiX > 0);
        Assert.Equal(result.Snapshot.DpiX, result.Snapshot.DpiY);
    }

    [Fact]
    public void ResolverPrefersSavedDevice()
    {
        var native = new FakeMonitorNativeApi
        {
            Infos =
            [
                new NativeMonitorInfo(new nint(1), "DISPLAY1", new MonitorPixelRect(0, 0, 1920, 1040)),
                new NativeMonitorInfo(new nint(2), "DISPLAY2", new MonitorPixelRect(1920, 0, 3520, 900)),
            ],
        };

        MonitorResolutionResult result = new MonitorWorkAreaResolver(native).Resolve("DISPLAY2", new nint(10));

        Assert.True(result.Succeeded);
        Assert.Equal(new nint(2), result.Snapshot.Handle);
        Assert.Equal("DISPLAY2", result.Snapshot.DeviceName);
    }

    [Fact]
    public void ResolverFallsBackToNearestWindowWhenSavedDeviceIsGone()
    {
        var native = new FakeMonitorNativeApi
        {
            Infos = [new NativeMonitorInfo(new nint(2), "DISPLAY2", new MonitorPixelRect(-1600, 0, 0, 900))],
            WindowMonitor = new nint(2),
        };

        MonitorResolutionResult result = new MonitorWorkAreaResolver(native).Resolve("DISPLAY1", new nint(10));

        Assert.True(result.Succeeded);
        Assert.Equal("DISPLAY2", result.Snapshot.DeviceName);
    }

    [Fact]
    public void ResolverStopsOnDpiFailure()
    {
        var native = new FakeMonitorNativeApi
        {
            Infos = [new NativeMonitorInfo(new nint(1), "DISPLAY1", new MonitorPixelRect(0, 0, 1920, 1040))],
            DpiSucceeds = false,
        };

        MonitorResolutionResult result = new MonitorWorkAreaResolver(native).Resolve("DISPLAY1", new nint(10));

        Assert.False(result.Succeeded);
        Assert.Equal("GetScaleFactorForMonitor", result.FailureStage);
    }

    [Fact]
    public void DragResolutionUsesCenterPointBeforeWindowHandle()
    {
        var native = new FakeMonitorNativeApi
        {
            Infos =
            [
                new NativeMonitorInfo(new nint(1), "DISPLAY1", new MonitorPixelRect(0, 0, 1920, 1040)),
                new NativeMonitorInfo(new nint(2), "DISPLAY2", new MonitorPixelRect(1920, 0, 3520, 900)),
            ],
            WindowMonitor = new nint(1),
            PointMonitor = new nint(2),
        };

        MonitorResolutionResult result = new MonitorWorkAreaResolver(native)
            .ResolveForDrag(new nint(10), (2500, 300));

        Assert.True(result.Succeeded);
        Assert.Equal("DISPLAY2", result.Snapshot.DeviceName);
        Assert.Equal(1, native.MonitorFromPointCalls);
        Assert.Equal(0, native.MonitorFromWindowCalls);
    }

    private sealed class FakeMonitorNativeApi : IMonitorNativeApi
    {
        internal IReadOnlyList<NativeMonitorInfo> Infos { get; init; } = [];
        internal nint WindowMonitor { get; init; }
        internal nint PointMonitor { get; init; }
        internal bool DpiSucceeds { get; init; } = true;
        internal int MonitorFromWindowCalls { get; private set; }
        internal int MonitorFromPointCalls { get; private set; }

        public bool TryEnumerateMonitors(ICollection<nint> handles, out int errorCode)
        {
            foreach (NativeMonitorInfo info in Infos) handles.Add(info.Handle);
            errorCode = 0;
            return true;
        }

        public bool TryGetMonitorInfo(nint monitor, out NativeMonitorInfo info, out int errorCode)
        {
            info = Infos.FirstOrDefault(candidate => candidate.Handle == monitor);
            errorCode = info.Handle == 0 ? 6 : 0;
            return info.Handle != 0;
        }

        public bool TryGetEffectiveDpi(nint monitor, out uint dpiX, out uint dpiY, out int hresult)
        {
            dpiX = DpiSucceeds ? 168u : 0u;
            dpiY = DpiSucceeds ? 168u : 0u;
            hresult = DpiSucceeds ? 0 : unchecked((int)0x80004005);
            return DpiSucceeds;
        }

        public nint MonitorFromWindow(nint windowHandle)
        {
            MonitorFromWindowCalls++;
            return WindowMonitor != 0 ? WindowMonitor : Infos[0].Handle;
        }

        public nint MonitorFromPoint(int x, int y)
        {
            MonitorFromPointCalls++;
            return PointMonitor != 0 ? PointMonitor : Infos[0].Handle;
        }
    }
}
