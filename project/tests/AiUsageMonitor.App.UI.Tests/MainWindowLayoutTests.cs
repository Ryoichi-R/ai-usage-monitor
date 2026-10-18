using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using static AiUsageMonitor.App.UI.Tests.MainWindowTestSupport;

namespace AiUsageMonitor.App.UI.Tests;

// WPF版MainWindowLayoutTestsの意図をAvalonia Headless（Skia描画・Interフォント）へ移植したもの。
// 文字幅はWPF版のSegoe UIではなく同梱のInterで測る。
public sealed class MainWindowLayoutTests
{
    private static readonly double[] ScaleSequence = [75d, 100d, 125d, 150d, 175d, 200d];

    [AvaloniaFact]
    public void ClaudeFreshnessContractStringsFitLogical138Dip()
    {
        string[] values =
        [
            "CLI 23:59",
            "CLI 12/31 23:59",
            "CLI最終 23:59",
            "CLI最終 12/31 23:59",
            "SL受信 23:59",
            "SL受信 12/31 23:59",
            "SL最終 23:59",
            "SL最終 12/31 23:59",
        ];

        foreach (string value in values)
        {
            var text = new TextBlock
            {
                Text = value,
                FontSize = 11,
                FontFeatures = FontFeatureCollection.Parse("+tnum"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            text.Measure(Size.Infinity);

            Assert.True(text.DesiredSize.Width <= 138d, $"{value} requires {text.DesiredSize.Width:F2} DIP.");
        }
    }

    [AvaloniaFact]
    public void UsageRowUsesRequestedAlignmentGuides()
    {
        (Window host, ContentPresenter presenter) = PresentTemplate(
            "UsageRowTemplate",
            Row("7D", "67", "RESET 07/29 02:02", .67),
            260,
            52);
        try
        {
            TextBlock label = Named<TextBlock>(presenter, "WindowLabel");
            StackPanel valuePanel = Named<StackPanel>(presenter, "UsageValuePanel");
            Grid progressTrack = Named<Grid>(presenter, "UsageProgressTrack");
            Assert.Equal(1, Grid.GetColumn(label));
            Assert.Equal(HorizontalAlignment.Left, label.HorizontalAlignment);
            Assert.InRange(BoundsIn(label, presenter).Left, 29.5, 30.5);
            Assert.InRange(BoundsIn(valuePanel, presenter).Right, 121.5, 122.5);
            Assert.InRange(BoundsIn(progressTrack, presenter).Left, 29.5, 30.5);
            Assert.InRange(BoundsIn(progressTrack, presenter).Right, 259.5, 260.5);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void MonetaryCardUsesMiddleColumnAndThreeVerticalLines()
    {
        (Window host, ContentPresenter presenter) = PresentTemplate(
            "MonetaryCardTemplate",
            new MonetaryCardViewModel { Title = "クレジット残高", Value = "$0.00", Secondary = "現在の残高" },
            260,
            double.NaN);
        try
        {
            Border card = Named<Border>(presenter, "MonetaryCard");
            TextBlock title = Named<TextBlock>(presenter, "MonetaryTitle");
            TextBlock value = Named<TextBlock>(presenter, "MonetaryValue");
            TextBlock secondary = Named<TextBlock>(presenter, "MonetarySecondary");

            Assert.InRange(BoundsIn(card, presenter).Left, 29.5, 30.5);
            Assert.InRange(card.Bounds.Width, 91.5, 92.5);
            Assert.Equal(0, Grid.GetRow(title));
            Assert.Equal(1, Grid.GetRow(value));
            Assert.Equal(2, Grid.GetRow(secondary));
            Assert.Equal(TextAlignment.Right, value.TextAlignment);
            Assert.Equal(TextAlignment.Right, secondary.TextAlignment);
            Assert.True(BoundsIn(title, presenter).Top < BoundsIn(value, presenter).Top);
            Assert.True(BoundsIn(value, presenter).Top < BoundsIn(secondary, presenter).Top);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void UsageRowKeepsResetInDedicatedSecondLineColumn()
    {
        (Window host, ContentPresenter presenter) = PresentTemplate(
            "UsageRowTemplate",
            Row("7D", "71", "RESET 08/02 09:24", .71),
            260,
            52);
        try
        {
            TextBlock reset = Named<TextBlock>(presenter, "ResetText");
            Assert.Equal(1, Grid.GetRow(reset));
            Assert.Equal(2, Grid.GetColumn(reset));
            Assert.DoesNotContain(Descendants<Control>(presenter), control => control.Name == "InlineFreshnessText");
            TextBlock remaining = Named<TextBlock>(presenter, "RemainingLabel");
            Assert.Equal("残り", remaining.Text);
            Assert.True(remaining.Bounds.Width > 0);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void SingleRowCodexAndMultiRowClaudeKeepFreshnessInAlignedHeaders()
    {
        DateTimeOffset now = new(2026, 7, 26, 21, 8, 0, TimeSpan.FromHours(9));
        TimeZoneInfo tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
        var viewModel = new UsageViewModel(() => now, tokyo) { ShowCodex = true, ShowClaude = true };
        viewModel.Apply(HeaderSnapshot(UsageProvider.Codex, now.AddMinutes(-5), includeWeekly: false));
        viewModel.Apply(HeaderSnapshot(UsageProvider.Claude, now.AddMinutes(-6), includeWeekly: true));
        Assert.Single(viewModel.Codex.Rows);
        Assert.Equal(2, viewModel.Claude.Rows.Count);

        MainWindow window = ShowLaidOut(viewModel, 100);
        try
        {
            Border root = Named<Border>(window, "Root");
            StackPanel standard = Named<StackPanel>(window, "StandardContentPanel");
            TextBlock codex = Assert.Single(Descendants<TextBlock>(standard), text => text.Text == "取得 21:03");
            TextBlock claude = Assert.Single(Descendants<TextBlock>(standard), text => text.Text == "取得 21:02");
            var codexHeader = Assert.IsType<Grid>(codex.Parent);
            var claudeHeader = Assert.IsType<Grid>(claude.Parent);

            Assert.Equal(BoundsIn(codex, root).Left, BoundsIn(claude, root).Left, 3);
            Assert.InRange(BoundsIn(codex, codexHeader).Left, 121.5, 122.5);
            Assert.InRange(BoundsIn(claude, claudeHeader).Left, 121.5, 122.5);
            Assert.Equal(TextAlignment.Right, codex.TextAlignment);
            Assert.Equal(TextAlignment.Right, claude.TextAlignment);
            Assert.Equal(138d, codex.MaxWidth);
            Assert.Equal(138d, claude.MaxWidth);
            Assert.InRange(BoundsIn(codex, codexHeader).Right, 259, 261);
            Assert.InRange(BoundsIn(claude, claudeHeader).Right, 259, 261);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SeverityBrushResourcesKeepExistingColorsAndDriveRowStyles()
    {
        var viewModel = CreateMaxDisplayViewModel();
        MainWindow window = ShowLaidOut(viewModel, 100);
        try
        {
            Assert.Equal(Color.FromArgb(255, 125, 200, 255), Brush(window, "AccentBrush").Color);
            Assert.Equal(Color.FromArgb(255, 255, 183, 77), Brush(window, "WarnBrush").Color);
            Assert.Equal(Color.FromArgb(255, 255, 107, 107), Brush(window, "DangerBrush").Color);
            Assert.Equal(Color.FromArgb(255, 176, 176, 176), Brush(window, "MutedBrush").Color);

            // 5h: 残り18% → Warning、weekly: 残り0% → Danger。値の文字とバーが同じ色になる。
            StackPanel standard = Named<StackPanel>(window, "StandardContentPanel");
            TextBlock warningValue = Descendants<TextBlock>(standard).First(text => text.Text == "18");
            TextBlock dangerValue = Descendants<TextBlock>(standard).First(text => text.Text == "0");
            Assert.Equal(Brush(window, "WarnBrush").Color, Assert.IsAssignableFrom<ISolidColorBrush>(warningValue.Foreground).Color);
            Assert.Equal(Brush(window, "DangerBrush").Color, Assert.IsAssignableFrom<ISolidColorBrush>(dangerValue.Foreground).Color);
            Rectangle[] bars = Descendants<Rectangle>(standard).Where(bar => bar.Classes.Contains("severity")).ToArray();
            Assert.Contains(bars, bar => ((ISolidColorBrush)bar.Fill!).Color == Brush(window, "WarnBrush").Color);
            Assert.Contains(bars, bar => ((ISolidColorBrush)bar.Fill!).Color == Brush(window, "DangerBrush").Color);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(125.5)]
    [InlineData(150)]
    [InlineData(175)]
    [InlineData(200)]
    public void ApplySettingsScalesRootAndSyncsWindowWidth(double scalePercent)
    {
        MainWindow window = ShowLaidOut(CreateMaxDisplayViewModel(), scalePercent);
        try
        {
            double scale = scalePercent / 100d;
            Border root = Named<Border>(window, "Root");
            LayoutTransformControl rootScale = Named<LayoutTransformControl>(window, "RootScale");

            // Root論理幅は倍率に依存せず不変。倍率はRootを包むLayoutTransformControlへ集約する。
            Assert.Equal(MainWindow.StandardWidgetWidthDip, root.Width, 3);
            var transform = Assert.IsType<ScaleTransform>(rootScale.LayoutTransform);
            Assert.Equal(scale, transform.ScaleX, 3);
            Assert.Equal(scale, transform.ScaleY, 3);
            Assert.Equal(MainWindow.StandardWidgetWidthDip * scale, window.Width, 3);

            TextBlock header = Named<TextBlock>(window, "CodexHeader");
            (double effX, double effY) = EffectiveScale(header, root);
            Assert.Equal(1d, effX, 3);
            Assert.Equal(1d, effY, 3);
            (double windowX, double windowY) = EffectiveScale(root, window);
            Assert.Equal(scale, windowX, 3);
            Assert.Equal(scale, windowY, 3);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RepeatedScalesDoNotAccumulate()
    {
        var window = new MainWindow { DataContext = CreateMaxDisplayViewModel() };
        window.Show();
        try
        {
            Border root = Named<Border>(window, "Root");
            LayoutTransformControl rootScale = Named<LayoutTransformControl>(window, "RootScale");
            foreach (double percent in ScaleSequence)
            {
                window.ApplySettings(new AppSettings { UiScalePercent = percent }, reposition: false);
                Settle(window);
                double scale = percent / 100d;
                Assert.Equal(scale, Assert.IsType<ScaleTransform>(rootScale.LayoutTransform).ScaleX, 3);
                Assert.Equal(MainWindow.StandardWidgetWidthDip * scale, window.Width, 3);
                Assert.Equal(MainWindow.StandardWidgetWidthDip, root.Width, 3);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(150)]
    [InlineData(200)]
    public void VisibleMarksStayWithinRootBounds(double scalePercent)
    {
        MainWindow window = ShowLaidOut(CreateMaxDisplayViewModel(), scalePercent);
        try
        {
            AssertMarksInsideRoot(window, $"{scalePercent}%");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void DisplayConfigurationsStayWithinRootBounds(bool showCodex, bool showClaude)
    {
        var viewModel = CreateMaxDisplayViewModel();
        viewModel.ShowCodex = showCodex;
        viewModel.ShowClaude = showClaude;
        MainWindow window = ShowLaidOut(viewModel, 200);
        try
        {
            AssertMarksInsideRoot(window, $"codex={showCodex} claude={showClaude}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void LoadingAndErrorStatesStayWithinRootBounds()
    {
        foreach (UsageViewModel viewModel in new[] { CreateLoadingViewModel(), CreateLongErrorViewModel() })
        {
            MainWindow window = ShowLaidOut(viewModel, 200);
            try
            {
                AssertMarksInsideRoot(window, "loading/error");
            }
            finally
            {
                window.Close();
            }
        }
    }

    [AvaloniaTheory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public void LogicalHeightFollowsVisibleProviders(string provider)
    {
        MainWindow both = ShowLaidOut(CreateMaxDisplayViewModel(), 100);
        var singleViewModel = CreateMaxDisplayViewModel();
        singleViewModel.ShowCodex = provider == "Codex";
        singleViewModel.ShowClaude = provider == "Claude";
        MainWindow single = ShowLaidOut(singleViewModel, 100);
        try
        {
            double bothHeight = Named<Border>(both, "Root").Bounds.Height;
            double singleHeight = Named<Border>(single, "Root").Bounds.Height;
            Assert.True(singleHeight < bothHeight, $"{provider}-only height {singleHeight} should be < both {bothHeight}.");
        }
        finally
        {
            single.Close();
            both.Close();
        }
    }

    [AvaloniaFact]
    public void TextDesiredWidthsFitLogicalContentArea()
    {
        foreach (UsageViewModel viewModel in new[]
        {
            CreateMaxDisplayViewModel(),
            CreateLoadingViewModel(),
            CreateLongErrorViewModel(),
        })
        {
            MainWindow window = ShowLaidOut(viewModel, 200);
            try
            {
                Border root = Named<Border>(window, "Root");
                double availableWidth = root.Bounds.Width - root.Padding.Left - root.Padding.Right;
                foreach (TextBlock text in Descendants<TextBlock>(root).Where(text => text.IsEffectivelyVisible))
                {
                    Assert.True(
                        text.DesiredSize.Width <= availableWidth + 0.5,
                        $"TextBlock '{text.Text}' desired width {text.DesiredSize.Width} exceeds {availableWidth}.");
                }
            }
            finally
            {
                window.Close();
            }
        }
    }

    private static void AssertMarksInsideRoot(MainWindow window, string context)
    {
        Border root = Named<Border>(window, "Root");
        foreach (Control element in Descendants<Control>(root).Where(control =>
            control is TextBlock or Rectangle && control.IsEffectivelyVisible && control.Bounds.Width > 0))
        {
            Rect bounds = BoundsIn(element, root);
            string label = element is TextBlock text ? text.Text ?? string.Empty : element.GetType().Name;
            Assert.True(bounds.Right <= root.Bounds.Width + 0.5, $"'{label}' overflows Root right edge ({context}).");
            Assert.True(bounds.Left >= -0.5, $"'{label}' overflows Root left edge ({context}).");
            Assert.True(bounds.Bottom <= root.Bounds.Height + 0.5, $"'{label}' overflows Root bottom edge ({context}).");
        }
    }

    private static ISolidColorBrush Brush(MainWindow window, string key) =>
        Assert.IsAssignableFrom<ISolidColorBrush>(window.Resources[key]);

    internal static (Window Host, ContentPresenter Presenter) PresentTemplate(
        string key,
        object content,
        double width,
        double height)
    {
        var templateSource = new MainWindow();
        var template = Assert.IsAssignableFrom<IDataTemplate>(templateSource.Resources[key]);
        var presenter = new ContentPresenter
        {
            Content = content,
            ContentTemplate = template,
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var host = new Window { Content = presenter, Width = width + 40, Height = 400 };
        host.Show();
        Settle(host);
        return (host, presenter);
    }

    internal static UsageRowViewModel Row(string label, string value, string secondary, double fraction) => new()
    {
        Label = label,
        Value = value,
        Secondary = secondary,
        Fraction = fraction,
        Severity = UsageSeverity.Accent,
    };

    private static UsageSnapshot HeaderSnapshot(UsageProvider provider, DateTimeOffset successfulAt, bool includeWeekly)
    {
        UsageWindowSnapshot fiveHour = new(null, null, "five_hour", 25,
            UsageWindowPolicy.FiveHourDurationMinutes, successfulAt.AddHours(1), null);
        UsageWindowSnapshot[] windows = includeWeekly
            ? [fiveHour, new(null, null, "weekly", 50, UsageWindowPolicy.SevenDayDurationMinutes, successfulAt.AddDays(1), null)]
            : [fiveHour];
        return new(provider, successfulAt, successfulAt, UsageAvailability.Available, null, null, windows, null, false, successfulAt);
    }
}
