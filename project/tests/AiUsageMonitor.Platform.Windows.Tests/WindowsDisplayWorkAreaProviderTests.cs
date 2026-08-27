using AiUsageMonitor.Platform.Windows.Window;

namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class WindowsDisplayWorkAreaProviderTests
{
    [Fact]
    public void ResolveMapsSnapshotToWidgetWorkAreaWithScaleFromDpi()
    {
        var native = new FakeMonitorNativeApi
        {
            Info = new NativeMonitorInfo(new nint(1), "DISPLAY1", new MonitorPixelRect(10, 20, 1930, 1100)),
            DpiX = 144,
            DpiY = 144,
        };
        var provider = new WindowsDisplayWorkAreaProvider(new MonitorWorkAreaResolver(native));

        WorkAreaResolution result = provider.Resolve(savedDisplayId: null, windowHandle: 0, centerPointPixels: (0, 0));

        Assert.True(result.Succeeded);
        Assert.Equal("DISPLAY1", result.Area.DisplayId);
        Assert.Equal(10, result.Area.Left);
        Assert.Equal(20, result.Area.Top);
        Assert.Equal(1920, result.Area.Width);
        Assert.Equal(1080, result.Area.Height);
        Assert.Equal(1.5, result.Area.ScaleX);
        Assert.Equal(1.5, result.Area.ScaleY);
    }

    [Fact]
    public void ResolveSurfacesTheNativeFailureStageAndErrorCode()
    {
        var native = new FakeMonitorNativeApi { EnumerateSucceeds = false, EnumerateErrorCode = 87 };
        var provider = new WindowsDisplayWorkAreaProvider(new MonitorWorkAreaResolver(native));

        WorkAreaResolution result = provider.Resolve(savedDisplayId: null, windowHandle: 0);

        Assert.False(result.Succeeded);
        Assert.Equal("EnumDisplayMonitors", result.FailureStage);
        Assert.Equal(87, result.ErrorCode);
    }

    private sealed class FakeMonitorNativeApi : IMonitorNativeApi
    {
        public bool EnumerateSucceeds { get; set; } = true;
        public int EnumerateErrorCode { get; set; }
        public NativeMonitorInfo Info { get; set; } = new(new nint(1), "DISPLAY1", new MonitorPixelRect(0, 0, 1920, 1080));
        public uint DpiX { get; set; } = 96;
        public uint DpiY { get; set; } = 96;

        public bool TryEnumerateMonitors(ICollection<nint> handles, out int errorCode)
        {
            errorCode = EnumerateErrorCode;
            if (!EnumerateSucceeds) return false;
            handles.Add(Info.Handle);
            return true;
        }

        public bool TryGetMonitorInfo(nint monitor, out NativeMonitorInfo info, out int errorCode)
        {
            errorCode = 0;
            info = Info;
            return true;
        }

        public bool TryGetEffectiveDpi(nint monitor, out uint dpiX, out uint dpiY, out int hresult)
        {
            hresult = 0;
            dpiX = DpiX;
            dpiY = DpiY;
            return true;
        }

        public nint MonitorFromWindow(nint windowHandle) => Info.Handle;

        public nint MonitorFromPoint(int x, int y) => Info.Handle;
    }
}
