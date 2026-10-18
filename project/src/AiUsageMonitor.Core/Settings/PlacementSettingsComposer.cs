namespace AiUsageMonitor.Core.Settings;

public static class PlacementSettingsComposer
{
    public static AppSettings UsePreset(AppSettings settings) => settings with
    {
        PlacementMode = PlacementMode.Preset,
        CustomLeftFraction = null,
        CustomTopFraction = null,
    };

    public static AppSettings UseCapturedPosition(AppSettings settings, AppSettings capturedPosition) => settings with
    {
        MonitorDeviceName = capturedPosition.MonitorDeviceName,
        MonitorStableId = capturedPosition.MonitorStableId,
        PlacementMode = PlacementMode.Custom,
        CustomLeftFraction = capturedPosition.CustomLeftFraction,
        CustomTopFraction = capturedPosition.CustomTopFraction,
    };
}
