namespace AiUsageMonitor.Platform;

public readonly record struct WidgetWorkArea(
    string DisplayId,
    int Left,
    int Top,
    int Width,
    int Height,
    double ScaleX,
    double ScaleY);

public readonly record struct WorkAreaResolution(
    bool Succeeded,
    WidgetWorkArea Area,
    string? FailureStage,
    int ErrorCode)
{
    public static WorkAreaResolution Failure(string stage, int errorCode = 0) =>
        new(false, default, stage, errorCode);

    public static WorkAreaResolution Success(WidgetWorkArea area) =>
        new(true, area, null, 0);
}

/// <summary>
/// 複数ディスプレイの作業領域・スケール・回転追従を解決する抽象。
/// Windows実装はEnumDisplayMonitors + GetScaleFactorForMonitor、
/// macOS実装はNSScreen.screensのvisibleFrame / backingScaleFactorを使う。
/// </summary>
public interface IDisplayWorkAreaProvider
{
    WorkAreaResolution Resolve(
        string? savedDisplayId,
        nint windowHandle,
        (int X, int Y)? centerPointPixels = null,
        bool preferCenterPoint = false);
}
