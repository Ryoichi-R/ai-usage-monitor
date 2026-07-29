using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.App.Tests;

// 層B: 実際にShow()し、Windowとしての振る舞い(ActualWidth/ActualHeight・SizeToContent・
// クライアント境界クリップ・DPI込み実効倍率)を確認する。ClickThroughHelper/Forms.Screenの
// 本番経路をそのまま通す。非対話環境ではCategory=Interactiveで除外できるが、ローカル検証では必ず実行する。
[Trait("Category", "Interactive")]
public sealed class MainWindowDisplayTests
{
    private static readonly double[] WorkAreaScales =
        [75d, 100d, 125d, 150d, 175d, 200d];
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;

    [Theory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(125.5)]
    [InlineData(150)]
    [InlineData(175)]
    [InlineData(200)]
    public void Shown_ActualWidthFollowsScale(double scalePercent)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            using var host = ShownWindow(scalePercent, out MainWindow window, out Border root);
            double scale = scalePercent / 100d;

            AssertClose(MainWindow.BaseWidgetWidthDip * scale, window.ActualWidth, 1.0);

            // Root変形後幅とWindowクライアント幅の差が1 DIP以内(幅の二重管理が破綻していない)。
            double rootScaledWidth = root.RenderSize.Width * scale;
            Assert.True(Math.Abs(rootScaledWidth - window.ActualWidth) <= 1.0,
                $"Root scaled width {rootScaledWidth} vs client width {window.ActualWidth}.");
        });
    }

    [Fact]
    public void Shown_HeightGrowsWithScale()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            using var host100 = ShownWindow(100, out MainWindow window100, out _);
            double height100 = window100.ActualHeight;

            using var host200 = ShownWindow(200, out MainWindow window200, out _);
            double height200 = window200.ActualHeight;

            Assert.True(height200 > height100, $"200% height {height200} should exceed 100% height {height100}.");
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
    public void Shown_SizeToContentHeightMatchesScaledRoot(double scalePercent)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            using var host = ShownWindow(scalePercent, out MainWindow window, out Border root);
            Rect rootBounds = root.TransformToAncestor(window).TransformBounds(new Rect(root.RenderSize));

            AssertClose(rootBounds.Height, window.ActualHeight, 1.0);
            AssertClose(rootBounds.Bottom, window.ActualHeight, 1.0);
        });
    }

    [Fact]
    public void Shown_EffectiveScaleToWindowIsScale()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            using var host = ShownWindow(200, out MainWindow window, out _);
            var header = Assert.IsType<TextBlock>(window.FindName("CodexHeader"));

            (double effX, double effY) = MainWindowScaleTestSupport.EffectiveScale(header, window);

            // 現行不具合(Window側だけ2、文字側は1)なら effX==1 となり失敗する。
            Assert.Equal(2d, effX, 3);
            Assert.Equal(2d, effY, 3);
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
    public void Shown_VisibleElementsStayWithinClientBounds(double scalePercent)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            using var host = ShownWindow(scalePercent, out MainWindow window, out _);
            var clientBounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);

            // TextBlockと利用率Rectangleの両方について、右端・下端のクライアント境界内包を確認する。
            foreach (FrameworkElement element in VisibleMarks(window))
            {
                Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                string label = element is TextBlock text ? text.Text : element.GetType().Name;
                Assert.True(bounds.Right <= clientBounds.Right + 1.0, $"'{label}' clips right edge at {scalePercent}%.");
                Assert.True(bounds.Bottom <= clientBounds.Bottom + 1.0, $"'{label}' clips bottom edge at {scalePercent}%.");
                Assert.True(bounds.Left >= -1.0, $"'{label}' clips left edge at {scalePercent}%.");
            }
        });
    }

    [Theory]
    [InlineData("Codex")]
    [InlineData("Claude")]
    public void Shown_SingleProvider_HeightFollowsContent(string provider)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var viewModel = MainWindowScaleTestSupport.CreateMaxDisplayViewModel();
            viewModel.ShowCodex = provider == "Codex";
            viewModel.ShowClaude = provider == "Claude";

            using var bothHost = ShownWindow(new AppSettings { UiScalePercent = 100 }, out MainWindow both, out _);
            double bothHeight = both.ActualHeight;

            var single = new MainWindow { DataContext = viewModel };
            using var host = Show(single, new AppSettings { UiScalePercent = 100 });
            // 片方のみ表示は両方表示より低い(高さが内容へ追従する)。
            Assert.True(single.ActualHeight < bothHeight, $"{provider}-only height {single.ActualHeight} should be < both {bothHeight}.");
        });
    }

    [Fact]
    public void Shown_LoadingAndErrorStates_StayWithinClientBounds()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            foreach (UsageViewModel viewModel in new[]
            {
                MainWindowScaleTestSupport.CreateLoadingViewModel(),
                MainWindowScaleTestSupport.CreateLongErrorViewModel(),
            })
            {
                var window = new MainWindow { DataContext = viewModel };
                using var host = Show(window, new AppSettings { UiScalePercent = 200 });
                var clientBounds = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
                foreach (FrameworkElement element in VisibleMarks(window))
                {
                    Rect bounds = element.TransformToAncestor(window).TransformBounds(new Rect(element.RenderSize));
                    string label = element is TextBlock text ? text.Text : element.GetType().Name;
                    Assert.True(bounds.Right <= clientBounds.Right + 1.0, $"'{label}' clips right edge.");
                    Assert.True(bounds.Bottom <= clientBounds.Bottom + 1.0, $"'{label}' clips bottom edge.");
                    Assert.True(bounds.Left >= -1.0, $"'{label}' clips left edge.");
                }
            }
        });
    }

    [Theory]
    [InlineData(PlacementAnchor.TopRight, true, false)]
    [InlineData(PlacementAnchor.TopLeft, false, false)]
    [InlineData(PlacementAnchor.BottomRight, true, true)]
    [InlineData(PlacementAnchor.BottomLeft, false, true)]
    public void Shown_PresetAnchorKeepsMargin(PlacementAnchor anchor, bool right, bool bottom)
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var settings = new AppSettings
            {
                UiScalePercent = 100,
                Anchor = anchor,
                PlacementMode = PlacementMode.Preset,
                HorizontalMarginDip = 20,
                VerticalMarginDip = 16,
                ClickThrough = false,
            };
            using var host = ShownWindow(settings, out MainWindow window, out _);
            Rect area = PrimaryWorkAreaDip(window);

            double expectedLeft = right ? area.Right - window.ActualWidth - 20 : area.Left + 20;
            double expectedTop = bottom ? area.Bottom - window.ActualHeight - 16 : area.Top + 16;
            AssertClose(expectedLeft, window.Left, 1.0);
            AssertClose(expectedTop, window.Top, 1.0);
        });
    }

    [Fact]
    public void Shown_CustomFraction_IsPreservedAndNotRewritten()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var settings = new AppSettings
            {
                UiScalePercent = 100,
                PlacementMode = PlacementMode.Custom,
                CustomLeftFraction = 0.25,
                CustomTopFraction = 0.5,
                MonitorDeviceName = System.Windows.Forms.Screen.PrimaryScreen!.DeviceName,
                ClickThrough = false,
            };
            using var host = ShownWindow(settings, out MainWindow window, out _);
            Rect area = PrimaryWorkAreaDip(window);

            double expectedLeft = area.Left + (Math.Max(0, area.Width - window.ActualWidth) * 0.25);
            double expectedTop = area.Top + (Math.Max(0, area.Height - window.ActualHeight) * 0.5);
            AssertClose(expectedLeft, window.Left, 1.0);
            AssertClose(expectedTop, window.Top, 1.0);

            // 倍率適用でfraction設定値そのものは書き換わらない。
            Assert.Equal(0.25, window.Settings.CustomLeftFraction);
            Assert.Equal(0.5, window.Settings.CustomTopFraction);
        });
    }

    [Fact]
    public void Shown_AllScales_StayWithinWorkArea()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            // WorkingArea超過のclamp規則自体はCore(WidgetPlacementCalculatorTests)で網羅する。
            // 実表示では、いずれの倍率でもWindowが作業領域内へ収まる(はみ出さない)ことを確認する。
            foreach (double percent in WorkAreaScales)
            {
                using var host = ShownWindow(percent, out MainWindow window, out _);
                Rect area = PrimaryWorkAreaDip(window);
                Assert.True(window.Left >= area.Left - 1, $"{percent}%: Left {window.Left} < area {area.Left}.");
                Assert.True(window.Top >= area.Top - 1, $"{percent}%: Top {window.Top} < area {area.Top}.");
            }
        });
    }

    [Fact]
    public void Shown_InitialPositionIsNotOffscreen()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            using var host = ShownWindow(100, out MainWindow window, out _);
            var dpi = VisualTreeHelper.GetDpi(window);
            var screen = System.Windows.Forms.Screen.PrimaryScreen!;
            double areaLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
            double areaRight = screen.WorkingArea.Right / dpi.DpiScaleX;

            // 0寸法配置なら Left が右端-0=areaRight 付近に寄る。正しくは幅を引いた位置に収まる。
            Assert.True(window.Left >= areaLeft - 1, $"Left {window.Left} is offscreen.");
            Assert.True(window.Left + window.ActualWidth <= areaRight + 1, $"Right edge {window.Left + window.ActualWidth} exceeds work area.");
        });
    }

    [Fact]
    public void Shown_ApplySettingsTogglesTopmostAndClickThrough()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var initial = new AppSettings
            {
                UiScalePercent = 100,
                AlwaysOnTop = false,
                ClickThrough = false,
            };
            using var host = ShownWindow(initial, out MainWindow window, out _);
            nint handle = new WindowInteropHelper(window).Handle;

            Assert.False(window.Topmost);
            Assert.Equal(0, GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExTransparent);

            window.ApplySettings(initial with { AlwaysOnTop = true, ClickThrough = true }, reposition: false);

            Assert.True(window.Topmost);
            Assert.NotEqual(0, GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExTransparent);

            window.ApplySettings(initial, reposition: false);

            Assert.False(window.Topmost);
            Assert.Equal(0, GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExTransparent);
        });
    }

    private static WindowHost ShownWindow(double scalePercent, out MainWindow window, out Border root) =>
        ShownWindow(new AppSettings { UiScalePercent = scalePercent }, out window, out root);

    private static WindowHost ShownWindow(AppSettings settings, out MainWindow window, out Border root)
    {
        window = new MainWindow { DataContext = MainWindowScaleTestSupport.CreateMaxDisplayViewModel() };
        WindowHost host = Show(window, settings);
        root = Assert.IsType<Border>(window.FindName("Root"));
        return host;
    }

    private static WindowHost Show(MainWindow window, AppSettings settings)
    {
        window.ApplySettings(settings, reposition: false);
        window.Show();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        window.UpdateLayout();
        // 予約(Background優先度)を消化し、ContentRendered経由の配置を確定させる。
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        return new WindowHost(window);
    }

    // 位置・寸法はデバイスピクセル丸めで最大1 DIP程度ずれるため、許容差付きで比較する。
    private static void AssertClose(double expected, double actual, double toleranceDip) =>
        Assert.True(Math.Abs(expected - actual) <= toleranceDip, $"Expected {expected} ± {toleranceDip}, got {actual}.");

    private static Rect PrimaryWorkAreaDip(Window window)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        return new Rect(
            screen.WorkingArea.Left / dpi.DpiScaleX,
            screen.WorkingArea.Top / dpi.DpiScaleY,
            screen.WorkingArea.Width / dpi.DpiScaleX,
            screen.WorkingArea.Height / dpi.DpiScaleY);
    }

    // 可視のTextBlockと利用率Rectangle(幅>0)を返す。クリップ判定の対象要素。
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

    private sealed class WindowHost(Window window) : IDisposable
    {
        public void Dispose() => window.Close();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetWindowLongPtr(nint windowHandle, int index);
}
