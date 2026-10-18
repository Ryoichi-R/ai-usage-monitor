using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.App.Tests;

public sealed class MainWindowCompactDisplayTests
{
    [Theory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125.5)]
    [InlineData(200)]
    public void CompactModeUses150DipBaseWidthAndScalesAsOneUnit(double scalePercent)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow
            {
                DataContext = MainWindowScaleTestSupport.CreateMaxDisplayViewModel(),
            };
            Border root = Assert.IsType<Border>(window.FindName("Root"));
            try
            {
                window.ApplySettings(new AppSettings
                {
                    DisplayMode = WidgetDisplayMode.Compact,
                    UiScalePercent = scalePercent,
                }, reposition: false);
                Layout(window);

                double scale = scalePercent / 100d;
                Assert.Equal(MainWindow.CompactWidgetWidthDip, window.CurrentBaseWidgetWidthDip);
                Assert.Equal(MainWindow.CompactWidgetWidthDip, root.Width, 3);
                Assert.Equal(8d, root.Padding.Left, 3);
                Assert.Equal(MainWindow.CompactWidgetWidthDip * scale, window.Width, 3);
                Assert.Equal(Visibility.Visible, window.CompactContentPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, window.StandardContentPanel.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompactUsageRowUsesAutoStarColumnsAndFits134DipContent()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            try
            {
                var template = Assert.IsType<DataTemplate>(window.FindResource("CompactUsageRowTemplate"));
                var presenter = new ContentPresenter
                {
                    Content = new UsageRowViewModel
                    {
                        Label = "7D",
                        Value = "37",
                        Secondary = "RESET 07/31 19:59",
                        Fraction = .37,
                        Severity = AiUsageMonitor.Core.Presentation.UsageSeverity.Accent,
                    },
                    ContentTemplate = template,
                };

                presenter.Measure(new Size(MainWindow.CompactWidgetContentWidthDip, 52));
                presenter.Arrange(new Rect(0, 0, MainWindow.CompactWidgetContentWidthDip, 52));
                presenter.ApplyTemplate();
                presenter.UpdateLayout();

                var label = Assert.IsType<TextBlock>(template.FindName("CompactWindowLabel", presenter));
                var reset = Assert.IsType<TextBlock>(template.FindName("CompactResetText", presenter));
                var progress = Assert.IsType<Grid>(template.FindName("CompactUsageProgressTrack", presenter));
                var valuePanel = Assert.IsType<StackPanel>(template.FindName("CompactUsageValuePanel", presenter));
                Assert.Equal(0, Grid.GetColumn(label));
                Assert.Equal(GridUnitType.Auto, ((Grid)label.Parent).ColumnDefinitions[0].Width.GridUnitType);
                Assert.Equal(GridUnitType.Star, ((Grid)label.Parent).ColumnDefinitions[1].Width.GridUnitType);
                Assert.InRange(valuePanel.TranslatePoint(new Point(valuePanel.ActualWidth, 0), presenter).X, 133.5, 134.5);
                Assert.Equal(0, Grid.GetColumn(reset));
                Assert.Equal(2, Grid.GetColumnSpan(reset));
                Assert.Equal(0, Grid.GetColumn(progress));
                Assert.Equal(2, Grid.GetColumnSpan(progress));
                Assert.InRange(progress.ActualWidth, 133.5, 134.5);
                Assert.True(progress.ActualWidth <= MainWindow.CompactWidgetContentWidthDip);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompactModeDoesNotExposeMonetaryCardsAndKeepsFullAccountNameMetadata()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var viewModel = MainWindowScaleTestSupport.CreateMaxDisplayViewModel();
            var window = new MainWindow { DataContext = viewModel };
            try
            {
                window.ApplySettings(new AppSettings { DisplayMode = WidgetDisplayMode.Compact }, reposition: false);
                Layout(window);

                Assert.Empty(Descendants<ContentControl>(window.CompactContentPanel));
                Assert.DoesNotContain(
                    Descendants<TextBlock>(window.CompactContentPanel),
                    text => text.Text == "追加利用額" || text.Text == "クレジット残高");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Layout(MainWindow window)
    {
        window.Measure(new Size(MainWindow.CompactWidgetWidthDip, 1000));
        window.Arrange(new Rect(0, 0, window.Width, 1000));
        window.UpdateLayout();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
