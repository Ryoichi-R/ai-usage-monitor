using System.Globalization;
using AiUsageMonitor.Core.Presentation;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace AiUsageMonitor.App.UI.Converters;

/// <summary>
/// WPF版のSeverity DataTriggerの代替。Avaloniaでは<c>Classes.warning</c> / <c>Classes.danger</c>へbindし、
/// スタイル側でbrushを切り替える。
/// </summary>
public static class SeverityConverters
{
    public static IValueConverter IsWarning { get; } =
        new FuncValueConverter<UsageSeverity, bool>(severity => severity == UsageSeverity.Warning);

    public static IValueConverter IsDanger { get; } =
        new FuncValueConverter<UsageSeverity, bool>(severity => severity == UsageSeverity.Danger);
}

/// <summary>
/// 利用率バーの残量割合を横方向のScaleTransformへ変換する。WPF版では
/// <c>ScaleTransform.ScaleX</c>へ直接bindしていたが、AvaloniaのTransformはDataContextを
/// 継承しないため、Transform自体をbindする。
/// </summary>
public sealed class FractionToScaleTransformConverter : IValueConverter
{
    public static FractionToScaleTransformConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double fraction = value is double number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : 0;
        return new ScaleTransform(fraction, 1);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
