using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Windows.Window;

namespace AiUsageMonitor.App;

internal readonly record struct DisplayWorkArea(
    WorkArea LocalDipArea,
    MonitorWorkAreaSnapshot Snapshot);

internal readonly record struct PhysicalWindowPosition(int Left, int Top);

internal readonly record struct CapturedDisplayPosition(
    string DeviceName,
    double LeftFraction,
    double TopFraction);

/// <summary>
/// Win32の物理pixel snapshotと、Coreが扱うDIPの境界を一箇所に閉じ込める。
/// </summary>
internal sealed class DisplayWorkAreaProvider
{
    private readonly MonitorWorkAreaResolver _resolver;

    internal DisplayWorkAreaProvider(MonitorWorkAreaResolver? resolver = null) =>
        _resolver = resolver ?? new MonitorWorkAreaResolver();

    internal bool TryGetCurrent(
        string? savedDeviceName,
        nint windowHandle,
        (int X, int Y)? centerPointPixels,
        out DisplayWorkArea workArea,
        out MonitorResolutionResult resolution)
    {
        resolution = _resolver.Resolve(savedDeviceName, windowHandle, centerPointPixels);
        if (!resolution.Succeeded)
        {
            workArea = default;
            return false;
        }

        MonitorWorkAreaSnapshot snapshot = resolution.Snapshot;
        workArea = new DisplayWorkArea(
            ToLocalDipWorkArea(snapshot),
            snapshot);
        return true;
    }

    internal bool TryGetForDrag(
        nint windowHandle,
        (int X, int Y) centerPointPixels,
        out DisplayWorkArea workArea,
        out MonitorResolutionResult resolution)
    {
        resolution = _resolver.ResolveForDrag(windowHandle, centerPointPixels);
        if (!resolution.Succeeded)
        {
            workArea = default;
            return false;
        }

        MonitorWorkAreaSnapshot snapshot = resolution.Snapshot;
        workArea = new DisplayWorkArea(ToLocalDipWorkArea(snapshot), snapshot);
        return true;
    }

    internal static bool TryCaptureCustomPosition(
        MonitorWorkAreaSnapshot snapshot,
        MonitorPixelRect informationBounds,
        out CapturedDisplayPosition position)
    {
        MonitorPixelRect area = snapshot.WorkingArea;
        int availableWidth = Math.Max(0, area.Width - Math.Max(0, informationBounds.Width));
        int availableHeight = Math.Max(0, area.Height - Math.Max(0, informationBounds.Height));
        double left = availableWidth == 0
            ? 0
            : (informationBounds.Left - area.Left) / (double)availableWidth;
        double top = availableHeight == 0
            ? 0
            : (informationBounds.Top - area.Top) / (double)availableHeight;

        position = new CapturedDisplayPosition(
            snapshot.DeviceName,
            Math.Clamp(left, 0, 1),
            Math.Clamp(top, 0, 1));
        return double.IsFinite(position.LeftFraction) && double.IsFinite(position.TopFraction);
    }

    internal static PhysicalWindowPosition ToPhysicalWindowPosition(
        MonitorWorkAreaSnapshot snapshot,
        double informationLeftDip,
        double informationTopDip,
        double informationInsetXDip,
        double informationInsetYDip) =>
        new(
            snapshot.WorkingArea.Left +
                DipToPixel(informationLeftDip - informationInsetXDip, snapshot.DpiX),
            snapshot.WorkingArea.Top +
                DipToPixel(informationTopDip - informationInsetYDip, snapshot.DpiY));

    internal static MonitorPixelRect GetInformationBounds(
        MonitorWorkAreaSnapshot snapshot,
        MonitorPixelRect outerWindowBounds,
        double informationInsetXDip,
        double informationInsetYDip,
        double informationWidthDip,
        double informationHeightDip)
    {
        int left = outerWindowBounds.Left + DipToPixel(informationInsetXDip, snapshot.DpiX);
        int top = outerWindowBounds.Top + DipToPixel(informationInsetYDip, snapshot.DpiY);
        return new MonitorPixelRect(
            left,
            top,
            left + Math.Max(0, DipToPixel(informationWidthDip, snapshot.DpiX)),
            top + Math.Max(0, DipToPixel(informationHeightDip, snapshot.DpiY)));
    }

    internal static MonitorPixelRect ClampInformationBounds(
        MonitorWorkAreaSnapshot snapshot,
        MonitorPixelRect informationBounds)
    {
        MonitorPixelRect area = snapshot.WorkingArea;
        int maxLeft = area.Right - informationBounds.Width;
        int maxTop = area.Bottom - informationBounds.Height;
        int left = maxLeft < area.Left
            ? area.Left
            : Math.Clamp(informationBounds.Left, area.Left, maxLeft);
        int top = maxTop < area.Top
            ? area.Top
            : Math.Clamp(informationBounds.Top, area.Top, maxTop);
        return new MonitorPixelRect(
            left,
            top,
            left + informationBounds.Width,
            top + informationBounds.Height);
    }

    internal static int DipToPixel(double value, uint dpi) =>
        (int)Math.Round(value * dpi / 96d, MidpointRounding.AwayFromZero);

    private static double PixelsToDip(int value, uint dpi) => value * 96d / dpi;

    private static WorkArea ToLocalDipWorkArea(MonitorWorkAreaSnapshot snapshot) => new(
        0,
        0,
        PixelsToDip(snapshot.WorkingArea.Width, snapshot.DpiX),
        PixelsToDip(snapshot.WorkingArea.Height, snapshot.DpiY));
}
