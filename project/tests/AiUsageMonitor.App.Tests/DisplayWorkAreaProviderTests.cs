using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Windows.Window;

namespace AiUsageMonitor.App.Tests;

public sealed class DisplayWorkAreaProviderTests
{
    [Fact]
    public void CaptureUsesPhysicalPixelCoordinatesForMixedDpi()
    {
        var snapshot = new MonitorWorkAreaSnapshot(
            new nint(1),
            "DISPLAY2",
            new MonitorPixelRect(1920, 0, 3520, 900),
            168,
            168);

        bool result = DisplayWorkAreaProvider.TryCaptureCustomPosition(
            snapshot,
            new MonitorPixelRect(2620, 100, 2820, 300),
            out CapturedDisplayPosition captured);

        Assert.True(result);
        Assert.Equal("DISPLAY2", captured.DeviceName);
        Assert.Equal(.5, captured.LeftFraction, 3);
        Assert.Equal(100d / 700d, captured.TopFraction, 3);
    }

    [Fact]
    public void CurrentWorkAreaUsesTargetDpiInMonitorLocalCoordinates()
    {
        var native = new FakeMonitorNativeApi
        {
            Infos = [new NativeMonitorInfo(new nint(1), "DISPLAY2", new MonitorPixelRect(1920, 0, 3520, 900))],
            DpiX = 168,
            DpiY = 168,
        };
        var provider = new DisplayWorkAreaProvider(new MonitorWorkAreaResolver(native));

        bool result = provider.TryGetCurrent("DISPLAY2", new nint(42), null, out DisplayWorkArea area, out _);

        Assert.True(result);
        Assert.Equal(0, area.LocalDipArea.Left);
        Assert.Equal(0, area.LocalDipArea.Top);
        Assert.Equal(1600d * 96d / 168d, area.LocalDipArea.Width, 3);
        Assert.Equal(900d * 96d / 168d, area.LocalDipArea.Height, 3);
    }

    [Fact]
    public void PhysicalPlacementAddsMonitorOriginAfterTargetDpiConversion()
    {
        var snapshot = new MonitorWorkAreaSnapshot(
            new nint(1),
            "DISPLAY2",
            new MonitorPixelRect(1920, -900, 3520, 0),
            168,
            168);

        PhysicalWindowPosition position = DisplayWorkAreaProvider.ToPhysicalWindowPosition(
            snapshot,
            informationLeftDip: 700,
            informationTopDip: 200,
            informationInsetXDip: 10,
            informationInsetYDip: 20);

        Assert.Equal(3128, position.Left);
        Assert.Equal(-585, position.Top);
    }

    [Fact]
    public void InformationBoundsAndClampStayInPhysicalPixelSpace()
    {
        var snapshot = new MonitorWorkAreaSnapshot(
            new nint(1),
            "DISPLAY2",
            new MonitorPixelRect(-1600, 0, 0, 900),
            120,
            120);
        var outer = new MonitorPixelRect(-1700, 850, -1450, 1050);

        MonitorPixelRect information = DisplayWorkAreaProvider.GetInformationBounds(
            snapshot,
            outer,
            informationInsetXDip: 8,
            informationInsetYDip: 4,
            informationWidthDip: 160,
            informationHeightDip: 120);
        MonitorPixelRect clamped = DisplayWorkAreaProvider.ClampInformationBounds(snapshot, information);

        Assert.Equal(new MonitorPixelRect(-1690, 855, -1490, 1005), information);
        Assert.Equal(new MonitorPixelRect(-1600, 750, -1400, 900), clamped);
    }

    private sealed class FakeMonitorNativeApi : IMonitorNativeApi
    {
        internal IReadOnlyList<NativeMonitorInfo> Infos { get; init; } = [];
        internal uint DpiX { get; init; } = 96;
        internal uint DpiY { get; init; } = 96;

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
            dpiX = DpiX;
            dpiY = DpiY;
            hresult = 0;
            return true;
        }

        public nint MonitorFromWindow(nint windowHandle) => Infos[0].Handle;
        public nint MonitorFromPoint(int x, int y) => Infos[0].Handle;
    }
}
