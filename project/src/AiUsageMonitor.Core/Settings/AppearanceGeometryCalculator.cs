namespace AiUsageMonitor.Core.Settings;

public readonly record struct AppearanceGeometry(
    double InformationWidth,
    double InformationHeight,
    double ExpansionRatio,
    double MaskRatio,
    double InlineWidth,
    double InlineHeight,
    double SplitWidth,
    double SplitHeight,
    double InformationOffsetX,
    double InformationOffsetY)
{
    public bool HasExpansion => ExpansionRatio > 0;
}

public static class AppearanceGeometryCalculator
{
    public static AppearanceGeometry Calculate(
        double baseWidgetWidthDip,
        double informationHeightDip,
        double uiScalePercent,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        double scale = double.IsFinite(uiScalePercent) ? Math.Clamp(uiScalePercent, 75, 200) / 100d : 1;
        double informationWidth = Math.Max(0, baseWidgetWidthDip) * scale;
        double informationHeight = Math.Max(0, informationHeightDip);
        double ratio = settings.BackgroundFillMode == BackgroundFillMode.EdgeFade && settings.BackgroundEnabled
            ? Math.Clamp(settings.BackgroundEdgeFadePercent / 100d, 0.05, 0.5)
            : 0;
        double mask = ratio / (1d + (2d * ratio));
        return new(
            informationWidth,
            informationHeight,
            ratio,
            mask,
            informationWidth * (1d + (2d * ratio)),
            informationHeight * (1d + (2d * ratio)),
            informationWidth * (1d + (2d * ratio)),
            informationHeight * (1d + (2d * ratio)),
            informationWidth * ratio,
            informationHeight * ratio);
    }

    public static double CalculateMaximumInformationHeight(
        double workAreaHeightDip,
        double verticalMarginDip,
        double uiScalePercent)
    {
        double scale = double.IsFinite(uiScalePercent) ? Math.Clamp(uiScalePercent, 75, 200) / 100d : 1;
        return Math.Max(0, (workAreaHeightDip - 2d * Math.Max(0, verticalMarginDip)) / scale);
    }
}
