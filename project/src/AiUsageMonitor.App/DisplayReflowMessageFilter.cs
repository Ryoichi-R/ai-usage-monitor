namespace AiUsageMonitor.App;

internal static class DisplayReflowMessageFilter
{
    internal const int WmDisplayChange = 0x007E;
    internal const int WmSettingChange = 0x001A;
    internal const int SpiSetWorkArea = 0x002F;

    internal static bool IsDisplayOrWorkAreaChange(int message, nint wParam) =>
        message == WmDisplayChange ||
        (message == WmSettingChange && wParam.ToInt64() == SpiSetWorkArea);
}
