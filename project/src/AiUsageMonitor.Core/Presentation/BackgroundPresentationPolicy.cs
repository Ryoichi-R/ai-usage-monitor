using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.Core.Presentation;

public enum BackgroundPresentationMode
{
    None,
    Inline,
    Split,
}

public static class BackgroundPresentationPolicy
{
    public static BackgroundPresentationMode Evaluate(AppSettings settings, bool splitAvailable = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings normalized = settings.Normalized();
        if (!normalized.BackgroundEnabled || normalized.EffectiveBackgroundOpacity <= 0)
            return BackgroundPresentationMode.None;
        return splitAvailable && normalized.HideBackgroundBehindWindows && normalized.AlwaysOnTop && normalized.Opacity > 0
            ? BackgroundPresentationMode.Split
            : BackgroundPresentationMode.Inline;
    }
}
