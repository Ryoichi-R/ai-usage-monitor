using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.App.Tests;

// 層A: Window表示なしの論理レイアウト回帰。ActualWidth/ActualHeight・SizeToContent・
// クライアント境界クリップは層B(MainWindowDisplayTests)で確認する。
public sealed class MainWindowLayoutTests
{
    private static readonly double[] ScaleSequence =
        [75d, 100d, 125d, 150d, 175d, 200d];
    private static readonly double[] FreshnessScaleSequence = [100d, 200d];

    [Fact]
    public void ClaudeFreshnessContractStringsFitLogical138DipAt100And200Percent()
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

        MainWindowScaleTestSupport.RunInSta(() =>
        {
            foreach (double scale in FreshnessScaleSequence)
            {
                foreach (string value in values)
                {
                    var text = new TextBlock
                    {
                        Text = value,
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 11,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
                    Typography.SetNumeralAlignment(
                        text,
                        FontNumeralAlignment.Tabular);
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

                    Assert.True(
                        text.DesiredSize.Width <= 138d,
                        $"{value} requires {text.DesiredSize.Width:F2} DIP at {scale:F0}%.");
                }
            }
        });
    }

    [Fact]
    public void UsageRow_UsesRequestedAlignmentGuides()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            var template = Assert.IsType<DataTemplate>(window.FindResource("UsageRowTemplate"));
            var presenter = new ContentPresenter
            {
                Content = new UsageRowViewModel
                {
                    Label = "7D",
                    Value = "67",
                    Secondary = "RESET 07/29 02:02",
                    Fraction = .67,
                    Severity = AiUsageMonitor.Core.Presentation.UsageSeverity.Accent,
                },
                ContentTemplate = template,
            };

            presenter.Measure(new Size(260, 52));
            presenter.Arrange(new Rect(0, 0, 260, 52));
            presenter.ApplyTemplate();
            presenter.UpdateLayout();

            var label = Assert.IsType<TextBlock>(template.FindName("WindowLabel", presenter));
            var valuePanel = Assert.IsType<StackPanel>(
                template.FindName("UsageValuePanel", presenter));
            var progressTrack = Assert.IsType<Grid>(
                template.FindName("UsageProgressTrack", presenter));
            Assert.Equal(1, Grid.GetColumn(label));
            Assert.Equal(HorizontalAlignment.Left, label.HorizontalAlignment);
            Assert.InRange(
                label.TranslatePoint(new Point(0, 0), presenter).X,
                29.5,
                30.5);
            Assert.InRange(
                valuePanel.TranslatePoint(
                    new Point(valuePanel.ActualWidth, 0),
                    presenter).X,
                121.5,
                122.5);
            Assert.InRange(
                progressTrack.TranslatePoint(new Point(0, 0), presenter).X,
                29.5,
                30.5);
            Assert.InRange(
                progressTrack.TranslatePoint(
                    new Point(progressTrack.ActualWidth, 0),
                    presenter).X,
                259.5,
                260.5);
            window.Close();
        });
    }

    [Fact]
    public void MonetaryCard_UsesMiddleColumnAndThreeVerticalLines()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            try
            {
                var template = Assert.IsType<DataTemplate>(
                    window.FindResource("MonetaryCardTemplate"));
                var presenter = new ContentPresenter
                {
                    Content = new MonetaryCardViewModel
                    {
                        Title = "クレジット残高",
                        Value = "$0.00",
                        Secondary = "現在の残高",
                    },
                    ContentTemplate = template,
                };

                presenter.Measure(new Size(260, double.PositiveInfinity));
                presenter.Arrange(
                    new Rect(0, 0, 260, presenter.DesiredSize.Height));
                presenter.ApplyTemplate();
                presenter.UpdateLayout();

                var card = Assert.IsType<Border>(
                    template.FindName("MonetaryCard", presenter));
                var title = Assert.IsType<TextBlock>(
                    template.FindName("MonetaryTitle", presenter));
                var value = Assert.IsType<TextBlock>(
                    template.FindName("MonetaryValue", presenter));
                var secondary = Assert.IsType<TextBlock>(
                    template.FindName("MonetarySecondary", presenter));

                Assert.InRange(
                    card.TranslatePoint(new Point(0, 0), presenter).X,
                    29.5,
                    30.5);
                Assert.InRange(card.ActualWidth, 91.5, 92.5);
                Assert.Equal(0, Grid.GetRow(title));
                Assert.Equal(1, Grid.GetRow(value));
                Assert.Equal(2, Grid.GetRow(secondary));
                Assert.Equal(TextAlignment.Right, value.TextAlignment);
                Assert.Equal(TextAlignment.Right, secondary.TextAlignment);
                Assert.True(
                    title.TranslatePoint(new Point(0, 0), presenter).Y <
                    value.TranslatePoint(new Point(0, 0), presenter).Y);
                Assert.True(
                    value.TranslatePoint(new Point(0, 0), presenter).Y <
                    secondary.TranslatePoint(new Point(0, 0), presenter).Y);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UsageRow_KeepsResetInDedicatedSecondLineColumn()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            try
            {
                var template = Assert.IsType<DataTemplate>(window.FindResource("UsageRowTemplate"));
                var presenter = new ContentPresenter
                {
                    Content = new UsageRowViewModel
                    {
                        Label = "7D",
                        Value = "71",
                        Secondary = "RESET 08/02 09:24",
                        Fraction = .71,
                        Severity = AiUsageMonitor.Core.Presentation.UsageSeverity.Accent,
                    },
                    ContentTemplate = template,
                };

                presenter.Measure(new Size(260, 52));
                presenter.Arrange(new Rect(0, 0, 260, 52));
                presenter.ApplyTemplate();
                presenter.UpdateLayout();

                var reset = Assert.IsType<TextBlock>(
                    template.FindName("ResetText", presenter));
                Assert.Equal(1, Grid.GetRow(reset));
                Assert.Equal(2, Grid.GetColumn(reset));
                Assert.Null(template.FindName("InlineFreshnessText", presenter));
                var remaining = Assert.IsType<TextBlock>(
                    template.FindName("RemainingLabel", presenter));
                Assert.Equal("残り", remaining.Text);
                Assert.False(remaining.IsArrangeValid && remaining.ActualWidth <= 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SingleRowCodexAndMultiRowClaudeKeepFreshnessInAlignedHeaders()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            DateTimeOffset now =
                new(2026, 7, 26, 21, 8, 0, TimeSpan.FromHours(9));
            TimeZoneInfo tokyo =
                TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");
            var viewModel = new UsageViewModel(
                () => now,
                tokyo)
            { ShowCodex = true, ShowClaude = true };

            viewModel.Apply(Snapshot(
                UsageProvider.Codex,
                now.AddMinutes(-5),
                includeWeekly: false));
            viewModel.Apply(Snapshot(
                UsageProvider.Claude,
                now.AddMinutes(-6),
                includeWeekly: true));

            Assert.Single(viewModel.Codex.Rows);
            Assert.Equal(2, viewModel.Claude.Rows.Count);
            Assert.Equal(Visibility.Visible, viewModel.Codex.FreshnessVisibility);
            Assert.Equal(Visibility.Visible, viewModel.Claude.FreshnessVisibility);

            MainWindow window = CreateLaidOutWindow(
                viewModel,
                100,
                out Border root);
            try
            {
                TextBlock codex = Assert.Single(
                    Descendants<TextBlock>((StackPanel)window.FindName("StandardContentPanel")!),
                    text => text.Text == "取得 21:03");
                TextBlock claude = Assert.Single(
                    Descendants<TextBlock>((StackPanel)window.FindName("StandardContentPanel")!),
                    text => text.Text == "取得 21:02");
                var codexHeader = Assert.IsType<Grid>(
                    VisualTreeHelper.GetParent(codex));
                var claudeHeader = Assert.IsType<Grid>(
                    VisualTreeHelper.GetParent(claude));

                Assert.Equal(
                    codex.TranslatePoint(new Point(0, 0), root).X,
                    claude.TranslatePoint(new Point(0, 0), root).X,
                    3);
                Assert.InRange(
                    codex.TranslatePoint(new Point(0, 0), codexHeader).X,
                    121.5,
                    122.5);
                Assert.InRange(
                    claude.TranslatePoint(new Point(0, 0), claudeHeader).X,
                    121.5,
                    122.5);
                Assert.Equal(TextAlignment.Right, codex.TextAlignment);
                Assert.Equal(TextAlignment.Right, claude.TextAlignment);
                Assert.Equal(138d, codex.MaxWidth);
                Assert.Equal(138d, claude.MaxWidth);
                Assert.True(codex.ActualWidth >= 80d);
                Assert.True(claude.ActualWidth >= 80d);
                Assert.InRange(
                    codex.TranslatePoint(
                        new Point(codex.ActualWidth, 0),
                        codexHeader).X,
                    259,
                    261);
                Assert.InRange(
                    claude.TranslatePoint(
                        new Point(claude.ActualWidth, 0),
                        claudeHeader).X,
                    259,
                    261);
            }
            finally
            {
                window.Close();
            }
        });

        static UsageSnapshot Snapshot(
            UsageProvider provider,
            DateTimeOffset successfulAt,
            bool includeWeekly)
        {
            UsageWindowSnapshot fiveHour = new(
                null,
                null,
                "five_hour",
                25,
                UsageWindowPolicy.FiveHourDurationMinutes,
                successfulAt.AddHours(1),
                null);
            UsageWindowSnapshot[] windows = includeWeekly
                ?
                [
                    fiveHour,
                    new(
                        null,
                        null,
                        "weekly",
                        50,
                        UsageWindowPolicy.SevenDayDurationMinutes,
                        successfulAt.AddDays(1),
                        null),
                ]
                : [fiveHour];

            return new(
                provider,
                successfulAt,
                successfulAt,
                UsageAvailability.Available,
                null,
                null,
                windows,
                null,
                false,
                successfulAt);
        }
    }

    [Fact]
    public void MaxFixtureUsesFixedTokyoClockAndNextDayReset()
    {
        UsageViewModel viewModel =
            MainWindowScaleTestSupport.CreateMaxDisplayViewModel();

        Assert.StartsWith(
            "RESET 07/25 00:13",
            viewModel.Codex.Rows[0].Secondary,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "RESET 07/25 00:13",
            viewModel.Claude.Rows[0].Secondary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SeverityBrushResourcesKeepExistingColors()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();
            try
            {
                Assert.Equal(
                    Color.FromArgb(255, 125, 200, 255),
                    Assert.IsType<SolidColorBrush>(
                        window.Resources["AccentBrush"]).Color);
                Assert.Equal(
                    Color.FromArgb(255, 255, 183, 77),
                    Assert.IsType<SolidColorBrush>(
                        window.Resources["WarnBrush"]).Color);
                Assert.Equal(
                    Color.FromArgb(255, 255, 107, 107),
                    Assert.IsType<SolidColorBrush>(
                        window.Resources["DangerBrush"]).Color);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(125.5)]
    [InlineData(150)]
    [InlineData(175)]
    [InlineData(200)]
    public void ApplySettings_ScalesRootAndSyncsWindowWidth(double scalePercent)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = CreateLaidOutWindow(scalePercent, out Border root);
            double scale = scalePercent / 100d;

            // Root論理幅は倍率に依存せず不変。設定漏れ(NaN)なら失敗する。
            Assert.False(double.IsNaN(root.Width));
            Assert.Equal(MainWindow.StandardWidgetWidthDip, root.Width, 3);

            var transform = Assert.IsType<ScaleTransform>(root.LayoutTransform);
            Assert.Equal(scale, transform.ScaleX, 3);
            Assert.Equal(scale, transform.ScaleY, 3);

            // Window自身の倍率はIdentityのまま。子側で拡大する。
            Assert.True(window.LayoutTransform is null || IsIdentity(window.LayoutTransform));

            // 設定値としてのWindow.Widthが論理幅×倍率。
            Assert.Equal(MainWindow.StandardWidgetWidthDip * scale, window.Width, 3);

            // 倍率はRoot.LayoutTransformに集約され、Root配下では独立した拡大が起きない。
            // Root→Windowの実効倍率(=scale)は表示が必要なため層Bで確認する。
            var header = Assert.IsType<TextBlock>(window.FindName("CodexHeader"));
            (double effX, double effY) = MainWindowScaleTestSupport.EffectiveScale(header, root);
            Assert.Equal(1d, effX, 3);
            Assert.Equal(1d, effY, 3);

            window.Close();
        });
    }

    [Fact]
    public void ApplySettings_RepeatedScales_DoNotAccumulate()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow { DataContext = MainWindowScaleTestSupport.CreateMaxDisplayViewModel() };
            Border root = Assert.IsType<Border>(window.FindName("Root"));

            foreach (double percent in ScaleSequence)
            {
                window.ApplySettings(new AppSettings { UiScalePercent = percent }, reposition: false);
                Layout(window);
                double scale = percent / 100d;
                var transform = Assert.IsType<ScaleTransform>(root.LayoutTransform);
                Assert.Equal(scale, transform.ScaleX, 3);
                Assert.Equal(MainWindow.StandardWidgetWidthDip * scale, window.Width, 3);
                Assert.Equal(MainWindow.StandardWidgetWidthDip, root.Width, 3);
            }

            window.Close();
        });
    }

    [Fact]
    public void ApplySettings_HundredPercent_KeepsBaselineWidth()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = CreateLaidOutWindow(100, out Border root);
            Assert.Equal(280d, window.Width, 3);
            Assert.Equal(280d, root.Width, 3);
            var transform = Assert.IsType<ScaleTransform>(root.LayoutTransform);
            Assert.Equal(1d, transform.ScaleX, 3);
            window.Close();
        });
    }

    [Theory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(175)]
    [InlineData(200)]
    public void ScaledRoot_VisibleMarksStayWithinRootBounds(double scalePercent)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = CreateLaidOutWindow(MainWindowScaleTestSupport.CreateMaxDisplayViewModel(), scalePercent, out Border root);

            // TextBlockと利用率Rectangleの両方について、右端・左端・下端の内包を確認する。
            foreach (FrameworkElement element in VisibleMarks(root))
            {
                Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                string label = element is TextBlock text ? text.Text : element.GetType().Name;
                Assert.True(bounds.Right <= root.RenderSize.Width + 0.5, $"'{label}' overflows Root right edge at {scalePercent}%.");
                Assert.True(bounds.Left >= -0.5, $"'{label}' overflows Root left edge at {scalePercent}%.");
                Assert.True(bounds.Bottom <= root.RenderSize.Height + 0.5, $"'{label}' overflows Root bottom edge at {scalePercent}%.");
            }

            window.Close();
        });
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ScaledRoot_DisplayConfigurations_StayWithinRootBounds(bool showCodex, bool showClaude)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var viewModel = MainWindowScaleTestSupport.CreateMaxDisplayViewModel();
            viewModel.ShowCodex = showCodex;
            viewModel.ShowClaude = showClaude;
            var window = CreateLaidOutWindow(viewModel, 200, out Border root);

            foreach (FrameworkElement element in VisibleMarks(root))
            {
                Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                string label = element is TextBlock text ? text.Text : element.GetType().Name;
                Assert.True(bounds.Right <= root.RenderSize.Width + 0.5, $"'{label}' overflows Root right edge ({bounds.Right} > {root.RenderSize.Width}).");
            }

            window.Close();
        });
    }

    [Fact]
    public void ScaledRoot_LoadingAndErrorStates_StayWithinRootBounds()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            foreach (UsageViewModel viewModel in new[]
            {
                MainWindowScaleTestSupport.CreateLoadingViewModel(),
                MainWindowScaleTestSupport.CreateLongErrorViewModel(),
            })
            {
                var window = CreateLaidOutWindow(viewModel, 200, out Border root);
                foreach (FrameworkElement element in VisibleMarks(root))
                {
                    Rect bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                    string label = element is TextBlock text ? text.Text : element.GetType().Name;
                    Assert.True(bounds.Right <= root.RenderSize.Width + 0.5, $"'{label}' overflows Root right edge.");
                }
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public void ScaledRoot_LogicalHeightFollowsVisibleProviders(string provider)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var bothViewModel = MainWindowScaleTestSupport.CreateMaxDisplayViewModel();
            var bothWindow = CreateLaidOutWindow(bothViewModel, 100, out Border bothRoot);
            double bothHeight = bothRoot.RenderSize.Height;

            var singleViewModel = MainWindowScaleTestSupport.CreateMaxDisplayViewModel();
            singleViewModel.ShowCodex = provider == "Codex";
            singleViewModel.ShowClaude = provider == "Claude";
            var singleWindow = CreateLaidOutWindow(singleViewModel, 100, out Border singleRoot);


            Assert.True(
                singleRoot.RenderSize.Height < bothHeight,
                $"{provider}-only logical height {singleRoot.RenderSize.Height} should be < both {bothHeight}.");

            singleWindow.Close();
            bothWindow.Close();
        });
    }

    [Fact]
    public void ScaledRoot_TextDesiredWidthsFitLogicalContentArea()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            foreach (UsageViewModel viewModel in new[]
            {
                MainWindowScaleTestSupport.CreateMaxDisplayViewModel(),
                MainWindowScaleTestSupport.CreateLoadingViewModel(),
                MainWindowScaleTestSupport.CreateLongErrorViewModel(),
            })
            {
                var window = CreateLaidOutWindow(viewModel, 200, out Border root);
                double availableWidth = root.RenderSize.Width - root.Padding.Left - root.Padding.Right;
                foreach (TextBlock text in Descendants<TextBlock>(root))
                {
                    if (text.Visibility != Visibility.Visible) continue;
                    Assert.True(
                        text.DesiredSize.Width <= availableWidth + 0.5,
                        $"TextBlock '{text.Text}' desired width {text.DesiredSize.Width} exceeds logical content width {availableWidth}.");
                }

                window.Close();
            }
        });
    }

    [Fact]
    public void LayoutContract_EmitsPlacementCompletionForSplitBackgroundConsumer()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = CreateLaidOutWindow(100, out _);
            try
            {
                window.Show();
                window.DrainPendingPlacementForTest();
                int completed = 0;
                window.PlacementCompleted += () => completed++;
                window.RequestRepositionForTest(fullApply: true);
                window.DrainPendingPlacementForTest();
                Assert.Equal(1, completed);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MainWindow CreateLaidOutWindow(UsageViewModel viewModel, double scalePercent, out Border root)
    {
        var window = new MainWindow { DataContext = viewModel };
        window.ApplySettings(new AppSettings { UiScalePercent = scalePercent }, reposition: false);
        Layout(window);
        root = Assert.IsType<Border>(window.FindName("Root"));
        return window;
    }

    private static MainWindow CreateLaidOutWindow(double scalePercent, out Border root) =>
        CreateLaidOutWindow(MainWindowScaleTestSupport.CreateMaxDisplayViewModel(), scalePercent, out root);

    private static void Layout(MainWindow window)
    {
        // 非表示のWindow自身をMeasure/Arrangeしても、HwndSourceがないためContentの
        // RenderSizeが0のままになる。層AではWindow直下のRootを直接レイアウトし、
        // 論理要素のDesiredSize・RenderSize・相対境界を実体として検証する。
        var host = Assert.IsType<Grid>(window.FindName("AppearanceHost"));
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
    }

    private static bool IsIdentity(Transform transform) =>
        transform.Value.IsIdentity;

    private static IEnumerable<FrameworkElement> VisibleMarks(DependencyObject root)
    {
        foreach (FrameworkElement element in Descendants<FrameworkElement>(root))
        {
            if (element is TextBlock or System.Windows.Shapes.Rectangle && element.ActualWidth > 0)
                yield return element;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
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
