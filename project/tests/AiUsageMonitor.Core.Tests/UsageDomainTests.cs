using System.Globalization;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Tests;

public sealed class UsageDomainTests
{
    [Theory]
    [InlineData(-10, 100)]
    [InlineData(0, 100)]
    [InlineData(25, 75)]
    [InlineData(100, 0)]
    [InlineData(150, 0)]
    public void RemainingPercentIsClamped(double used, double expected)
    {
        var window = new UsageWindowSnapshot(null, null, "primary", used, 300, null, null);
        Assert.Equal(expected, window.RemainingPercent);
    }

    [Theory]
    [InlineData(300, "5H")]
    [InlineData(10080, "7D")]
    [InlineData(1440, "1D")]
    [InlineData(90, "90M")]
    public void DurationProducesStableLabel(int duration, string expected)
    {
        var window = new UsageWindowSnapshot("fallback", "name", "primary", 0, duration, null, null);
        Assert.Equal(expected, UsageWindowFormatter.Label(window));
    }

    [Fact]
    public void NullDurationUsesNameThenIdThenSlot()
    {
        Assert.Equal("LIMIT NAME", UsageWindowFormatter.Label(new("id", "Limit Name", "primary", 0, null, null, null)));
        Assert.Equal("ID", UsageWindowFormatter.Label(new("id", null, "primary", 0, null, null, null)));
        Assert.Equal("SECONDARY", UsageWindowFormatter.Label(new(null, null, "secondary", 0, null, null, null)));
    }

    [Fact]
    public void SettingsClampPlacementAndPolling()
    {
        AppSettings result = new AppSettings { RefreshIntervalSeconds = 1, StartupTimeoutSeconds = 1000, HorizontalMarginDip = -1, VerticalMarginDip = 500, UiScalePercent = 300 }.Normalized();
        Assert.Equal(60, result.RefreshIntervalSeconds);
        Assert.Equal(120, result.StartupTimeoutSeconds);
        Assert.Equal(0, result.HorizontalMarginDip);
        Assert.Equal(200, result.VerticalMarginDip);
        Assert.Equal(200, result.UiScalePercent);
    }

    [Fact]
    public async Task ExistingSettingsDefaultCodexUsageDisplayToEnabled()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, """{"SchemaVersion":1,"ShowClaudeUsage":true}""");

            AppSettings result = await new FileSystemSettingsStore(path).LoadAsync();

            Assert.True(result.ShowCodexUsage);
            Assert.True(result.ShowClaudeUsage);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExistingSettingsWithoutUiScaleDefaultsToHundred()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, """{"SchemaVersion":1}""");

            AppSettings result = await new FileSystemSettingsStore(path).LoadAsync();

            Assert.Equal(100, result.UiScalePercent);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ExistingSettingsDefaultClaudeAcquisitionToAutomatic()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(path, """{"SchemaVersion":1,"ShowClaudeUsage":true}""");
            AppSettings result = await new FileSystemSettingsStore(path).LoadAsync();
            Assert.Equal(ClaudeUsageAcquisitionMode.Automatic, result.ClaudeUsageAcquisitionMode);
            Assert.Null(result.ClaudeExecutablePath);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ClaudeAcquisitionSettingsRoundTrip()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new FileSystemSettingsStore(path);
            await store.SaveAsync(new AppSettings
            {
                ClaudeUsageAcquisitionMode = ClaudeUsageAcquisitionMode.OfficialCliOnly,
                ClaudeExecutablePath = @" C:\Tools\claude.exe ",
                ClaudeSetupCompleted = true,
            });
            AppSettings result = await store.LoadAsync();
            Assert.Equal(ClaudeUsageAcquisitionMode.OfficialCliOnly, result.ClaudeUsageAcquisitionMode);
            Assert.Equal(@"C:\Tools\claude.exe", result.ClaudeExecutablePath);
            Assert.True(result.ClaudeSetupCompleted);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CodexUsageDisplayCanBeDisabledAndReloaded()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new FileSystemSettingsStore(path);
            await store.SaveAsync(new AppSettings { ShowCodexUsage = false });

            AppSettings result = await store.LoadAsync();

            Assert.False(result.ShowCodexUsage);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SettingsRoundTripUsesCultureIndependentJsonNumbers()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            var store = new FileSystemSettingsStore(path);
            await store.SaveAsync(new AppSettings
            {
                HorizontalMarginDip = 12.5,
                VerticalMarginDip = 7.25,
                UiScalePercent = 125.5,
            });

            string json = await File.ReadAllTextAsync(path);
            Assert.Contains("\"HorizontalMarginDip\": 12.5", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"HorizontalMarginDip\": 12,5", json, StringComparison.Ordinal);
            Assert.Contains("\"UiScalePercent\": 125.5", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\"UiScalePercent\": 125,5", json, StringComparison.Ordinal);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            AppSettings result = await store.LoadAsync();

            Assert.Equal(12.5, result.HorizontalMarginDip);
            Assert.Equal(7.25, result.VerticalMarginDip);
            Assert.Equal(125.5, result.UiScalePercent);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125.5)]
    [InlineData(200)]
    public async Task UiScaleRepresentativeValuesRoundTrip(double scale)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AiUsageMonitor.Tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new FileSystemSettingsStore(path);
            await store.SaveAsync(new AppSettings { UiScalePercent = scale });

            AppSettings result = await store.LoadAsync();

            Assert.Equal(scale, result.UiScalePercent);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PresetPlacementClearsStaleCustomCoordinates()
    {
        AppSettings result = PlacementSettingsComposer.UsePreset(new AppSettings
        {
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = .9,
            CustomTopFraction = .1,
        });

        Assert.Equal(PlacementMode.Preset, result.PlacementMode);
        Assert.Null(result.CustomLeftFraction);
        Assert.Null(result.CustomTopFraction);
    }

    [Fact]
    public void CapturedPositionUsesActualMonitorInsteadOfPendingSelection()
    {
        var controls = new AppSettings { MonitorDeviceName = "DISPLAY-PENDING", Anchor = PlacementAnchor.BottomRight };
        var captured = new AppSettings
        {
            MonitorDeviceName = "DISPLAY-ACTUAL",
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = .25,
            CustomTopFraction = .75,
        };

        AppSettings result = PlacementSettingsComposer.UseCapturedPosition(controls, captured);

        Assert.Equal(PlacementMode.Custom, result.PlacementMode);
        Assert.Equal("DISPLAY-ACTUAL", result.MonitorDeviceName);
        Assert.Equal(.25, result.CustomLeftFraction);
        Assert.Equal(.75, result.CustomTopFraction);
        Assert.Equal(PlacementAnchor.BottomRight, result.Anchor);
    }
}
