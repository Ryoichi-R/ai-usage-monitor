using System.Globalization;
using AiUsageMonitor.App.UI.Converters;
using AiUsageMonitor.Core.Presentation;
using Avalonia.Headless.XUnit;
using Avalonia.Media;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class UsageConvertersTests
{
    // ScaleTransformはAvaloniaObjectのため、Headlessセッションと同じUIスレッドで生成する。
    [AvaloniaTheory]
    [InlineData(0.25, 0.25)]
    [InlineData(-1.0, 0.0)]
    [InlineData(2.0, 1.0)]
    [InlineData(double.NaN, 0.0)]
    public void FractionIsClampedIntoAHorizontalScale(double fraction, double expected)
    {
        var transform = Assert.IsType<ScaleTransform>(FractionToScaleTransformConverter.Instance.Convert(
            fraction, typeof(ITransform), null, CultureInfo.InvariantCulture));

        Assert.Equal(expected, transform.ScaleX, 10);
        Assert.Equal(1d, transform.ScaleY, 10);
    }

    [AvaloniaFact]
    public void NonNumericFractionHidesTheBarAndConvertBackIsUnsupported()
    {
        var transform = Assert.IsType<ScaleTransform>(FractionToScaleTransformConverter.Instance.Convert(
            "not a number", typeof(ITransform), null, CultureInfo.InvariantCulture));
        Assert.Equal(0d, transform.ScaleX);
        Assert.Throws<NotSupportedException>(() => FractionToScaleTransformConverter.Instance.ConvertBack(
            transform, typeof(double), null, CultureInfo.InvariantCulture));
    }

    [AvaloniaTheory]
    [InlineData(UsageSeverity.Normal, false, false)]
    [InlineData(UsageSeverity.Accent, false, false)]
    [InlineData(UsageSeverity.Warning, true, false)]
    [InlineData(UsageSeverity.Danger, false, true)]
    public void SeverityMapsToExactlyOneStyleClass(UsageSeverity severity, bool warning, bool danger)
    {
        Assert.Equal(warning, SeverityConverters.IsWarning.Convert(severity, typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(danger, SeverityConverters.IsDanger.Convert(severity, typeof(bool), null, CultureInfo.InvariantCulture));
    }
}
