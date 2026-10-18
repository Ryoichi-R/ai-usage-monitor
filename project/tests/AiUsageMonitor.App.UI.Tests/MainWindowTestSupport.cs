using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AiUsageMonitor.App.UI.Tests;

/// <summary>
/// WPF版<c>MainWindowScaleTestSupport</c>のAvalonia移植。最大表示fixture、表示済みWindowの作成、
/// visual treeの探索を提供する。
/// </summary>
internal static class MainWindowTestSupport
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 10, 15, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    /// <summary>Codex / Claude両方に、LOW / LIMIT・長いリセット日時を含む最大級の表示を与えたViewModel。</summary>
    public static UsageViewModel CreateMaxDisplayViewModel()
    {
        var viewModel = new UsageViewModel(() => Now, Tokyo) { ShowCodex = true, ShowClaude = true };
        viewModel.Apply(MaxSnapshot(UsageProvider.Codex));
        viewModel.Apply(MaxSnapshot(UsageProvider.Claude));
        return viewModel;
    }

    /// <summary>両プロバイダーがLoading状態のViewModel。</summary>
    public static UsageViewModel CreateLoadingViewModel()
    {
        var viewModel = new UsageViewModel(() => Now, Tokyo) { ShowCodex = true, ShowClaude = true };
        viewModel.Apply(UsageSnapshot.Loading(UsageProvider.Codex, Now));
        viewModel.Apply(UsageSnapshot.Loading(UsageProvider.Claude, Now));
        return viewModel;
    }

    /// <summary>長いエラー状態文言を持つViewModel（文字列が最大化する状態）。</summary>
    public static UsageViewModel CreateLongErrorViewModel()
    {
        var viewModel = new UsageViewModel(() => Now, Tokyo) { ShowCodex = true, ShowClaude = true };
        viewModel.Apply(ErrorSnapshot(UsageProvider.Codex));
        viewModel.Apply(ErrorSnapshot(UsageProvider.Claude));
        return viewModel;
    }

    /// <summary>Windowを表示してレイアウトを確定させる。呼出側で<see cref="Window.Close"/>する。</summary>
    public static MainWindow ShowLaidOut(UsageViewModel viewModel, AppSettings settings)
    {
        var window = new MainWindow { DataContext = viewModel };
        window.ApplySettings(settings, reposition: false);
        window.Show();
        Settle(window);
        return window;
    }

    public static MainWindow ShowLaidOut(UsageViewModel viewModel, double scalePercent) =>
        ShowLaidOut(viewModel, new AppSettings { UiScalePercent = scalePercent });

    public static void Settle(TopLevel topLevel)
    {
        Dispatcher.UIThread.RunJobs();
        topLevel.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public static T Named<T>(Visual root, string name) where T : Control =>
        Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);

    public static IEnumerable<T> Descendants<T>(Visual root) => root.GetVisualDescendants().OfType<T>();

    /// <summary>要素の外形を祖先座標系へ変換した矩形。</summary>
    public static Rect BoundsIn(Visual element, Visual ancestor)
    {
        Matrix transform = element.TransformToVisual(ancestor)
            ?? throw new InvalidOperationException("Element is not a descendant of the ancestor.");
        return new Rect(element.Bounds.Size).TransformToAABB(transform);
    }

    /// <summary>要素から祖先への実効倍率を、3点変換で算出する。</summary>
    public static (double ScaleX, double ScaleY) EffectiveScale(Visual element, Visual ancestor)
    {
        Matrix transform = element.TransformToVisual(ancestor)
            ?? throw new InvalidOperationException("Element is not a descendant of the ancestor.");
        Point origin = transform.Transform(new Point(0, 0));
        Point unitX = transform.Transform(new Point(1, 0));
        Point unitY = transform.Transform(new Point(0, 1));
        return (Distance(unitX, origin), Distance(unitY, origin));
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static UsageSnapshot ErrorSnapshot(UsageProvider provider) => new(
        provider,
        Now,
        Now,
        UsageAvailability.Error,
        Reason: "LONG_ERROR_REASON_FOR_LAYOUT_STRESS",
        PlanType: null,
        Windows: [],
        Credits: null,
        IsStale: false,
        LastSuccessfulAt: null);

    private static UsageSnapshot MaxSnapshot(UsageProvider provider) => new(
        provider,
        Now,
        Now,
        UsageAvailability.Available,
        Reason: null,
        PlanType: "Pro",
        Windows:
        [
            new UsageWindowSnapshot("5h", "5-hour limit", "primary", UsedPercent: 82, WindowDurationMins: 300, ResetsAt: Now.AddHours(4).AddMinutes(58), ReachedType: null),
            new UsageWindowSnapshot("weekly", "weekly limit", "secondary", UsedPercent: 100, WindowDurationMins: 10080, ResetsAt: Now.AddDays(6).AddHours(13), ReachedType: "limit"),
        ],
        Credits: null,
        IsStale: false,
        LastSuccessfulAt: Now);
}
