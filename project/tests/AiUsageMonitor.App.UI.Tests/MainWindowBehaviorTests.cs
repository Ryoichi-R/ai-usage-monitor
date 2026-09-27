using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using static AiUsageMonitor.App.UI.Tests.MainWindowTestSupport;

namespace AiUsageMonitor.App.UI.Tests;

// WPF版MainWindowCompactDisplayTestsの意図と、MainWindowの表示側の振る舞い（背景外形、外観リソース、
// スクロール案内、再配置要求）をAvalonia Headlessで検証する。
public sealed class MainWindowBehaviorTests
{
    [AvaloniaTheory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125.5)]
    [InlineData(200)]
    public void CompactModeUses150DipBaseWidthAndScalesAsOneUnit(double scalePercent)
    {
        MainWindow window = ShowLaidOut(
            CreateMaxDisplayViewModel(),
            new AppSettings { DisplayMode = WidgetDisplayMode.Compact, UiScalePercent = scalePercent });
        try
        {
            Border root = Named<Border>(window, "Root");
            double scale = scalePercent / 100d;
            Assert.Equal(MainWindow.CompactWidgetWidthDip, window.CurrentBaseWidgetWidthDip);
            Assert.Equal(MainWindow.CompactWidgetWidthDip, root.Width, 3);
            Assert.Equal(MainWindow.CompactWidgetPaddingDip, root.Padding.Left, 3);
            Assert.Equal(MainWindow.CompactWidgetWidthDip * scale, window.Width, 3);
            Assert.True(Named<StackPanel>(window, "CompactContentPanel").IsVisible);
            Assert.False(Named<StackPanel>(window, "StandardContentPanel").IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CompactUsageRowUsesAutoStarColumnsAndFits134DipContent()
    {
        (Window host, ContentPresenter presenter) = MainWindowLayoutTests.PresentTemplate(
            "CompactUsageRowTemplate",
            MainWindowLayoutTests.Row("7D", "37", "RESET 07/31 19:59", .37),
            MainWindow.CompactWidgetContentWidthDip,
            52);
        try
        {
            TextBlock label = Named<TextBlock>(presenter, "CompactWindowLabel");
            TextBlock reset = Named<TextBlock>(presenter, "CompactResetText");
            Grid progress = Named<Grid>(presenter, "CompactUsageProgressTrack");
            StackPanel valuePanel = Named<StackPanel>(presenter, "CompactUsageValuePanel");
            var grid = Assert.IsType<Grid>(label.Parent);
            Assert.Equal(0, Grid.GetColumn(label));
            Assert.Equal(GridUnitType.Auto, grid.ColumnDefinitions[0].Width.GridUnitType);
            Assert.Equal(GridUnitType.Star, grid.ColumnDefinitions[1].Width.GridUnitType);
            Assert.InRange(BoundsIn(valuePanel, presenter).Right, 133.5, 134.5);
            Assert.Equal(0, Grid.GetColumn(reset));
            Assert.Equal(2, Grid.GetColumnSpan(reset));
            Assert.Equal(0, Grid.GetColumn(progress));
            Assert.Equal(2, Grid.GetColumnSpan(progress));
            Assert.InRange(progress.Bounds.Width, 133.5, 134.5);
        }
        finally
        {
            host.Close();
        }
    }

    [AvaloniaFact]
    public void CompactModeDoesNotExposeMonetaryCards()
    {
        MainWindow window = ShowLaidOut(
            CreateMaxDisplayViewModel(),
            new AppSettings { DisplayMode = WidgetDisplayMode.Compact });
        try
        {
            StackPanel compact = Named<StackPanel>(window, "CompactContentPanel");
            Assert.Empty(Descendants<ContentControl>(compact));
            Assert.DoesNotContain(
                Descendants<TextBlock>(compact),
                text => text.Text is "追加利用額" or "クレジット残高");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EdgeFadeWidensTheWindowAndSizesTheFadeHostAroundTheInformationArea()
    {
        MainWindow window = ShowLaidOut(CreateMaxDisplayViewModel(), new AppSettings
        {
            UiScalePercent = 150,
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
            BackgroundEdgeFadePercent = 20,
        });
        try
        {
            double information = MainWindow.StandardWidgetWidthDip * 1.5;
            Grid host = Named<Grid>(window, "AppearanceHost");
            Border fadeHost = Named<Border>(window, "BackgroundFadeHost");
            LayoutTransformControl rootScale = Named<LayoutTransformControl>(window, "RootScale");
            Assert.Equal(information * 1.4, host.Width, 3);
            Assert.Equal(information * 1.4, window.Width, 3);
            Assert.Equal(rootScale.Bounds.Height * 1.4, fadeHost.Height, 3);
            Assert.NotNull(host.OpacityMask);

            Rect bounds = window.GetInformationBoundsInScreenDip();
            Assert.Equal(information, bounds.Width, 3);
            Assert.Equal(rootScale.Bounds.Height, bounds.Height, 3);

            window.ApplySettings(new AppSettings { UiScalePercent = 150, BackgroundEnabled = true }, reposition: false);
            Settle(window);
            Assert.Equal(information, window.Width, 3);
            Assert.True(double.IsNaN(fadeHost.Height));
            Assert.Null(host.OpacityMask);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SplitBackgroundRemovesInlineFadeGeometryAndInlineRestoresIt()
    {
        AppSettings settings = new()
        {
            BackgroundEnabled = true,
            BackgroundFillMode = BackgroundFillMode.EdgeFade,
            BackgroundEdgeFadePercent = 20,
        };
        MainWindow window = ShowLaidOut(CreateMaxDisplayViewModel(), settings);
        try
        {
            int boundsChanges = 0;
            window.InformationBoundsChanged += () => boundsChanges++;
            Border surface = Named<Border>(window, "BackgroundSurface");

            window.SetInlineBackground(settings, BackgroundPresentationMode.Split);
            Settle(window);
            Assert.Equal(MainWindow.StandardWidgetWidthDip, window.Width, 3);
            Assert.Same(Brushes.Transparent, surface.Background);

            window.SetInlineBackground(settings, BackgroundPresentationMode.Inline);
            Settle(window);
            Assert.Equal(MainWindow.StandardWidgetWidthDip * 1.4, window.Width, 3);
            Assert.NotSame(Brushes.Transparent, surface.Background);
            Assert.True(boundsChanges >= 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AppearanceSettingsUpdateBrushesWithoutOwningNativeLayer()
    {
        MainWindow window = ShowLaidOut(CreateMaxDisplayViewModel(), new AppSettings
        {
            MutedColor = "#FF102030",
            AccentColor = "#FF405060",
            WarningColor = "#FF708090",
            DangerColor = "#FFA0B0C0",
            ForegroundColor = "#FFEEDDCC",
            FontFamilyName = "Inter",
            Opacity = 0.75,
            AlwaysOnTop = false,
        });
        try
        {
            Border root = Named<Border>(window, "Root");
            Assert.Equal(Color.Parse("#FF102030"), ((ISolidColorBrush)window.Resources["MutedBrush"]!).Color);
            Assert.Equal(Color.Parse("#FF405060"), ((ISolidColorBrush)window.Resources["AccentBrush"]!).Color);
            Assert.Equal(Color.Parse("#FF708090"), ((ISolidColorBrush)window.Resources["WarnBrush"]!).Color);
            Assert.Equal(Color.Parse("#FFA0B0C0"), ((ISolidColorBrush)window.Resources["DangerBrush"]!).Color);
            Assert.Equal(Color.Parse("#FFEEDDCC"), ((ISolidColorBrush)root.GetValue(TextElement.ForegroundProperty)!).Color);
            Assert.Equal("Inter", root.GetValue(TextElement.FontFamilyProperty).Name);
            Assert.Equal(0.75, window.Opacity, 3);
            Assert.False(window.Topmost);

            // 各行の値テキストは、更新されたAccent/Warn/Danger brushで再描画される。
            TextBlock warning = Descendants<TextBlock>(Named<StackPanel>(window, "StandardContentPanel"))
                .First(text => text.Text == "18");
            Assert.Equal(Color.Parse("#FF708090"), ((ISolidColorBrush)warning.Foreground!).Color);

            window.ApplySettings(new AppSettings(), reposition: false);
            Settle(window);
            Assert.False(root.IsSet(TextElement.ForegroundProperty));
            Assert.False(root.IsSet(TextElement.FontFamilyProperty));
            Assert.False(window.Topmost);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ScrollGuidanceAppearsOnlyWhileClickThroughBlocksRequiredScrolling()
    {
        MainWindow window = ShowLaidOut(CreateMaxDisplayViewModel(), new AppSettings { ClickThrough = true });
        try
        {
            Assert.False(window.IsScrollGuidanceVisible);

            // 作業領域を小さくすると最大高が下がり、内容のスクロールが必要になる。
            window.SetWorkAreaHeight(160);
            Settle(window);
            Assert.True(Named<Border>(window, "Root").MaxHeight < 200);
            Assert.True(window.IsScrollGuidanceVisible);

            window.ApplySettings(new AppSettings { ClickThrough = false }, reposition: false);
            window.SetWorkAreaHeight(160);
            Settle(window);
            Assert.False(window.IsScrollGuidanceVisible);

            window.SetWorkAreaHeight(double.NaN);
            window.SetWorkAreaHeight(-1);
            Assert.True(Named<Border>(window, "Root").MaxHeight < 200);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RepositionRequestsDistinguishFullApplyFromContentChanges()
    {
        var window = new MainWindow { DataContext = CreateMaxDisplayViewModel() };
        var requests = new List<bool>();
        window.RepositionRequested += requests.Add;
        try
        {
            window.ApplySettings(new AppSettings(), reposition: true);
            window.ApplySettings(new AppSettings(), reposition: false);
            Assert.Equal([true], requests);

            window.RepositionAfterContentChange();
            Assert.Equal([true, false], requests);
        }
        finally
        {
            window.Close();
        }
    }
}
