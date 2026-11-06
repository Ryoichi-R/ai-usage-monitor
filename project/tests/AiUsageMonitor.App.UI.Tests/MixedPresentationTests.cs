using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.WorkStatus;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using static AiUsageMonitor.App.UI.Tests.MainWindowTestSupport;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class MixedPresentationTests
{
    [AvaloniaTheory]
    [InlineData(WidgetDisplayMode.Standard, 75)]
    [InlineData(WidgetDisplayMode.Compact, 75)]
    [InlineData(WidgetDisplayMode.Standard, 200)]
    [InlineData(WidgetDisplayMode.Compact, 200)]
    [InlineData(WidgetDisplayMode.Standard, 100)]
    [InlineData(WidgetDisplayMode.Compact, 100)]
    [InlineData(WidgetDisplayMode.Standard, 150)]
    [InlineData(WidgetDisplayMode.Compact, 150)]
    public void WorkStatusStaysAboveBothUsageProvidersWithoutClipping(WidgetDisplayMode mode, double scale)
    {
        UsageViewModel view = CreateMaxDisplayViewModel();
        var model = new WorkStatusModel();
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 3; i++)
            model.Apply(new(1, "running-" + i, WorkProvider.CodexDesktop, "session-" + i, "t", WorkEventKind.Started, now, true));
        model.Apply(new(1, "waiting", WorkProvider.CodexDesktop, "waiting", "t", WorkEventKind.Waiting, now, true));
        model.Apply(new(1, "claude", WorkProvider.ClaudeCode, "claude", "t", WorkEventKind.StopCandidate, now, true));
        view.WorkStatus.Update(model, true, true, null, now);
        var window = ShowLaidOut(view, new AppSettings
        {
            DisplayMode = mode,
            UiScalePercent = scale,
            ShowClaudeUsage = true,
            ShowCodexWorkStatus = true,
            ShowClaudeWorkStatus = true,
            ClickThrough = false
        });
        try
        {
            Border root = Named<Border>(window, "Root");
            var texts = window.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => text.IsEffectivelyVisible && text.Opacity > 0 && text.Bounds.Height > 0).ToArray();
            string[] workLabels = ["Codex Desktop", "作業中 3", "入力待ち", "Claude Code", "応答終了"];
            var work = workLabels.Select(label => Assert.Single(texts, text => text.Text == label)).ToArray();
            var usage = texts.Where(text => text.Text is "CODEX" or "CLAUDE").ToArray();
            Assert.Equal(2, usage.Length);
            Assert.True(work.Max(text => BoundsIn(text, root).Bottom) <= usage.Min(text => BoundsIn(text, root).Top));
            Assert.All(work, text =>
            {
                Assert.True(BoundsIn(text, root).Left >= 0);
                Assert.True(BoundsIn(text, root).Right <= root.Bounds.Width + .5);
                Assert.True(text.TextLayout.Width <= text.Bounds.Width + .5);
            });
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "確認済み"));
        }
        finally { window.Close(); }
    }
}
