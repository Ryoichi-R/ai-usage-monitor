using System.Globalization;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.App.UI.Appearance;

public readonly record struct ArgbColor(byte A, byte R, byte G, byte B)
{
    public static ArgbColor Transparent { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// UIフレームワークに依存しない背景表現。塗りつぶし色と、縁フェード時のマスク比率を持つ。
/// WPF版<c>AppearanceBrushFactory</c>の計算部分をPhase 2で分離したもの。
/// </summary>
public readonly record struct AppearanceStyle(ArgbColor Fill, double? EdgeFadeMaskRatio)
{
    public static AppearanceStyle From(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings normalized = settings.Normalized();
        return new(FillColor(normalized), EdgeFadeMaskRatioFor(normalized));
    }

    private static ArgbColor FillColor(AppSettings normalized)
    {
        if (!normalized.BackgroundEnabled || normalized.EffectiveBackgroundOpacity <= 0)
            return ArgbColor.Transparent;

        // EffectiveBackgroundRgbはAppearanceSettingsValidatorにより常に大文字16進6桁へ正規化される。
        string rgb = normalized.EffectiveBackgroundRgb;
        byte alpha = (byte)Math.Round(
            Math.Clamp(normalized.EffectiveBackgroundOpacity, 0, 1) * byte.MaxValue,
            MidpointRounding.AwayFromZero);
        return new(
            alpha,
            byte.Parse(rgb.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(rgb.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(rgb.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static double? EdgeFadeMaskRatioFor(AppSettings normalized)
    {
        if (!normalized.BackgroundEnabled || normalized.BackgroundFillMode != BackgroundFillMode.EdgeFade)
            return null;
        double ratio = Math.Clamp(normalized.BackgroundEdgeFadePercent / 100d, 0.05, 0.5);
        return ratio / (1d + (2d * ratio));
    }
}
