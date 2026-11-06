using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.WorkStatus;
using Avalonia.Controls;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace AiUsageMonitor.App.UI.Tests;

public sealed partial class WorkStatusPresentationTests
{
    private static readonly Color[] IndicatorColors = [Color.Parse("#FFFFD54F"), Color.Parse("#FFFF6B6B"), Colors.White];
    [AvaloniaTheory]
    [InlineData(WidgetDisplayMode.Standard, 100)]
    [InlineData(WidgetDisplayMode.Compact, 100)]
    [InlineData(WidgetDisplayMode.Standard, 75)]
    [InlineData(WidgetDisplayMode.Compact, 75)]
    public void StatusCirclesHaveDistinctColorsAndTextUsesTheExistingMutedPalette(WidgetDisplayMode mode, double scale)
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        for (int i = 0; i < 3; i++)
            model.Apply(new(1, "start-" + i, WorkProvider.CodexDesktop, "running-" + i, "turn", WorkEventKind.Started, now, true));
        model.Apply(new(1, "wait", WorkProvider.CodexDesktop, "waiting", "turn", WorkEventKind.Waiting, now, true));
        model.Apply(new(1, "stop", WorkProvider.ClaudeCode, "ended", "turn", WorkEventKind.StopCandidate, now, true));
        var usage = MainWindowTestSupport.CreateMaxDisplayViewModel();
        usage.WorkStatus.Update(model, true, true, null, now);
        var settings = new AppSettings
        {
            DisplayMode = mode,
            UiScalePercent = scale,
            ShowCodexWorkStatus = true,
            ShowClaudeWorkStatus = true,
            ShowClaudeUsage = true,
            ForegroundColor = "#FFFF00FF"
        };
        var window = MainWindowTestSupport.ShowLaidOut(usage, settings);
        // A synthetic opaque backdrop makes the transparent widget preview legible.
        window.Background = Brushes.Black;
        try
        {
            var circles = StatusCircles(window);
            Assert.Equal(3, circles.Length);
            Assert.Equal(IndicatorColors, circles.Select(circle => Assert.IsAssignableFrom<ISolidColorBrush>(circle.Fill).Color));
            var text = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.Classes.Contains("work-status-text")).ToArray();
            Assert.Equal(5, text.Length);
            Assert.All(text, block =>
            {
                Assert.Equal(Color.Parse(settings.MutedColor), Assert.IsAssignableFrom<ISolidColorBrush>(block.Foreground).Color);
                Assert.True(block.Bounds.Width > 0);
                Assert.True(MainWindowTestSupport.BoundsIn(block, window).Right <= window.Bounds.Width + 1);
            });
            Assert.Equal("作業中 3 / 権限確認中", usage.WorkStatus.Rows[0].Status);
            Assert.Equal("応答終了", usage.WorkStatus.Rows[1].Status);
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains('!') == true);

            string? artifactRoot = Environment.GetEnvironmentVariable("AI_USAGE_MONITOR_TEST_ARTIFACTS_ROOT");
            if (!string.IsNullOrWhiteSpace(artifactRoot))
            {
                string directory = Path.Combine(artifactRoot, "work-status-previews");
                Directory.CreateDirectory(directory);
                MainWindowTestSupport.Settle(window);
                using var frame = window.CaptureRenderedFrame();
                Assert.NotNull(frame);
                frame.Save(Path.Combine(directory, $"{mode}-{scale}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AnswerAndResponseEndRefreshTheCircleWithoutKeepingTheOldColor()
    {
        var now = DateTimeOffset.UtcNow;
        var model = new WorkStatusModel();
        var usage = new UsageViewModel { ShowCodex = false, ShowClaude = false };
        var window = MainWindowTestSupport.ShowLaidOut(usage, new AppSettings
        {
            ShowCodexUsage = false,
            ShowClaudeUsage = false,
            ShowClaudeWorkStatus = true
        });
        try
        {
            var transitions = new[]
            {
                (WorkEventKind.Waiting, (bool?)true, "入力待ち", "#FFFF6B6B"),
                (WorkEventKind.Activity, (bool?)false, "作業中 1", "#FFFFD54F"),
                (WorkEventKind.StopCandidate, (bool?)null, "応答終了", "#FFFFFFFF")
            };
            for (int i = 0; i < transitions.Length; i++)
            {
                var (kind, pending, text, color) = transitions[i];
                model.Apply(new(1, "transition-" + i, WorkProvider.ClaudeCode, "question", "turn", kind,
                    now.AddSeconds(i), true, InputPending: pending));
                usage.WorkStatus.Update(model, false, true, null, now.AddSeconds(i));
                MainWindowTestSupport.Settle(window);
                Assert.Equal(text, usage.WorkStatus.Rows[1].Status);
                var circle = Assert.Single(StatusCircles(window), item => item.IsEffectivelyVisible);
                Assert.Equal(Color.Parse(color), Assert.IsAssignableFrom<ISolidColorBrush>(circle.Fill).Color);
            }
        }
        finally { window.Close(); }
    }

    private static Ellipse[] StatusCircles(MainWindow window) => window.GetVisualDescendants().OfType<Ellipse>()
        .Where(circle => circle.Classes.Contains("work-status-indicator")).ToArray();
}
