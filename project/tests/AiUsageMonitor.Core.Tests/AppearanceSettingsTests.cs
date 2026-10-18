using System.Text.Json;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.Core.Tests;

public sealed class AppearanceSettingsTests
{
    [Fact]
    public void DefaultsPreserveTransparentLegacyAppearance()
    {
        AppSettings settings = new AppSettings().Normalized();

        Assert.False(settings.BackgroundEnabled);
        Assert.Null(settings.FontFamilyName);
        Assert.Null(settings.ForegroundColor);
        Assert.Equal(AppearanceSettingsValidator.DefaultBackgroundColor, settings.BackgroundColor);
        Assert.Equal(.69, settings.BackgroundOpacity);
        Assert.Equal(BackgroundPresentationMode.None, BackgroundPresentationPolicy.Evaluate(settings));
    }

    [Theory]
    [InlineData("#123456", "#FF123456")]
    [InlineData(" #aBcDeF ", "#FFABCDEF")]
    [InlineData("#80123456", "#80123456")]
    public void ColorsNormalizeToUppercaseArgb(string input, string expected)
    {
        Assert.True(AppearanceSettingsValidator.TryNormalizeColor(input, out string normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("blue")]
    [InlineData("#123456789")]
    public void InvalidColorsAreRejected(string input)
    {
        Assert.False(AppearanceSettingsValidator.TryNormalizeColor(input, out _));
    }

    [Fact]
    public void InvalidLoadedValuesAreSafeAndValidationReportsThem()
    {
        AppSettings raw = new()
        {
            FontFamilyName = new string('x', 129),
            MutedColor = "invalid",
            BackgroundOpacity = double.NaN,
            BackgroundEdgeFadePercent = double.PositiveInfinity,
            BackgroundFillMode = (BackgroundFillMode)123,
        };

        AppSettings normalized = raw.Normalized();
        Assert.Null(normalized.FontFamilyName);
        Assert.Equal(AppearanceSettingsValidator.DefaultMutedColor, normalized.MutedColor);
        Assert.Equal(.69, normalized.BackgroundOpacity);
        Assert.Equal(22, normalized.BackgroundEdgeFadePercent);
        Assert.Equal(BackgroundFillMode.Solid, normalized.BackgroundFillMode);
        Assert.NotEmpty(AppearanceSettingsValidator.Validate(raw));
    }

    [Fact]
    public void PresentationPolicyRequiresAllSplitConditions()
    {
        AppSettings settings = new()
        {
            BackgroundEnabled = true,
            HideBackgroundBehindWindows = true,
            AlwaysOnTop = true,
        };

        Assert.Equal(BackgroundPresentationMode.Inline, BackgroundPresentationPolicy.Evaluate(settings));
        Assert.Equal(BackgroundPresentationMode.Split, BackgroundPresentationPolicy.Evaluate(settings, splitAvailable: true));
        Assert.Equal(BackgroundPresentationMode.Inline, BackgroundPresentationPolicy.Evaluate(
            settings with { AlwaysOnTop = false }, splitAvailable: true));
        Assert.Equal(BackgroundPresentationMode.None, BackgroundPresentationPolicy.Evaluate(
            settings with { BackgroundOpacity = 0 }, splitAvailable: true));
    }

    [Fact]
    public void GeometryExpandsOnlyBackgroundAndKeepsInformationSizeStable()
    {
        AppSettings settings = new() { BackgroundEnabled = true, BackgroundFillMode = BackgroundFillMode.EdgeFade };
        AppearanceGeometry geometry = AppearanceGeometryCalculator.Calculate(280, 200, 125.5, settings);

        Assert.Equal(351.4, geometry.InformationWidth, 6);
        Assert.Equal(22, geometry.ExpansionRatio * 100, 6);
        Assert.Equal(.22 / 1.44, geometry.MaskRatio, 6);
        Assert.Equal(351.4 * 1.44, geometry.InlineWidth, 6);
        Assert.Equal(200 * 1.44, geometry.InlineHeight, 6);
        Assert.Equal(geometry.InlineWidth, geometry.SplitWidth, 6);
        Assert.Equal(geometry.InlineHeight, geometry.SplitHeight, 6);
        Assert.Equal(351.4 * .22, geometry.InformationOffsetX, 6);
        Assert.Equal(AppearanceGeometryCalculator.CalculateMaximumInformationHeight(1000, 12, 125.5),
            AppearanceGeometryCalculator.CalculateMaximumInformationHeight(1000, 12, 125.5), 6);
    }

    [Fact]
    public void AppearancePropertiesRoundTripWithoutSchemaBumpOrLosingExtensionData()
    {
        AppSettings source = new()
        {
            FontFamilyName = "Segoe UI",
            ForegroundColor = "#102030",
            BackgroundEnabled = true,
            BackgroundColor = "#ABCDEF",
            BackgroundOpacity = .5,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
            BackgroundEdgeFadePercent = 50,
            HideBackgroundBehindWindows = true,
            ExtensionData = new() { ["Future"] = JsonDocument.Parse("true").RootElement },
        };

        AppSettings result = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(source))!.Normalized();

        Assert.Equal(2, result.SchemaVersion);
        Assert.Equal("#FF102030", result.ForegroundColor);
        Assert.Equal("#FFABCDEF", result.BackgroundColor);
        Assert.Equal(.5, result.BackgroundOpacity);
        Assert.Equal(BackgroundFillMode.EdgeFade, result.BackgroundFillMode);
        Assert.True(result.HideBackgroundBehindWindows);
        Assert.Contains("Future", result.ExtensionData!.Keys);
    }
}
