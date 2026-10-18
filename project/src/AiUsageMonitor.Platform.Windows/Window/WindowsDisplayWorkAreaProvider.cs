using AiUsageMonitor.Platform;

namespace AiUsageMonitor.Platform.Windows.Window;

/// <summary>EnumDisplayMonitors + GetScaleFactorForMonitorでIDisplayWorkAreaProviderへ適合させる。</summary>
public sealed class WindowsDisplayWorkAreaProvider : IDisplayWorkAreaProvider
{
    private const double StandardDpi = 96.0;
    private readonly MonitorWorkAreaResolver _resolver;

    public WindowsDisplayWorkAreaProvider()
        : this(new MonitorWorkAreaResolver())
    {
    }

    internal WindowsDisplayWorkAreaProvider(MonitorWorkAreaResolver resolver) => _resolver = resolver;

    public WorkAreaResolution Resolve(
        string? savedDisplayId,
        nint windowHandle,
        (int X, int Y)? centerPointPixels = null,
        bool preferCenterPoint = false)
    {
        MonitorResolutionResult result = _resolver.Resolve(savedDisplayId, windowHandle, centerPointPixels, preferCenterPoint);
        if (!result.Succeeded)
            return WorkAreaResolution.Failure(result.FailureStage ?? "UNKNOWN", result.ErrorCode);

        MonitorWorkAreaSnapshot snapshot = result.Snapshot;
        var area = new WidgetWorkArea(
            snapshot.DeviceName,
            snapshot.WorkingArea.Left,
            snapshot.WorkingArea.Top,
            snapshot.WorkingArea.Width,
            snapshot.WorkingArea.Height,
            snapshot.DpiX / StandardDpi,
            snapshot.DpiY / StandardDpi);
        return WorkAreaResolution.Success(area);
    }
}
