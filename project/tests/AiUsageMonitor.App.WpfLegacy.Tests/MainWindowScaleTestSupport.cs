using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.App.Tests;

/// <summary>
/// 倍率・配置テスト共通の支援。最大表示fixtureとSTA実行、実効倍率の3点測定を提供する。
/// </summary>
internal static class MainWindowScaleTestSupport
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 10, 15, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    /// <summary>Codex / Claude両方に、LOW / LIMIT・長いリセット日時を含む最大級の表示を与えたViewModel。</summary>
    public static UsageViewModel CreateMaxDisplayViewModel()
    {
        var viewModel = new UsageViewModel(
            () => Now,
            Tokyo)
        { ShowCodex = true, ShowClaude = true };
        viewModel.Apply(MaxSnapshot(UsageProvider.Codex));
        viewModel.Apply(MaxSnapshot(UsageProvider.Claude));
        return viewModel;
    }

    /// <summary>両プロバイダーがLoading状態のViewModel。</summary>
    public static UsageViewModel CreateLoadingViewModel()
    {
        var viewModel = new UsageViewModel(
            () => Now,
            Tokyo)
        { ShowCodex = true, ShowClaude = true };
        viewModel.Apply(UsageSnapshot.Loading(UsageProvider.Codex, Now));
        viewModel.Apply(UsageSnapshot.Loading(UsageProvider.Claude, Now));
        return viewModel;
    }

    /// <summary>長いエラー状態文言を持つViewModel（文字列が最大化する状態）。</summary>
    public static UsageViewModel CreateLongErrorViewModel()
    {
        var viewModel = new UsageViewModel(
            () => Now,
            Tokyo)
        { ShowCodex = true, ShowClaude = true };
        viewModel.Apply(ErrorSnapshot(UsageProvider.Codex));
        viewModel.Apply(ErrorSnapshot(UsageProvider.Claude));
        return viewModel;
    }

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

    /// <summary>要素から祖先への実効倍率を、3点変換で算出する。Matrixへのキャストは行わない。</summary>
    public static (double ScaleX, double ScaleY) EffectiveScale(Visual element, Visual ancestor)
    {
        GeneralTransform transform = element.TransformToAncestor(ancestor);
        Point origin = transform.Transform(new Point(0, 0));
        Point unitX = transform.Transform(new Point(1, 0));
        Point unitY = transform.Transform(new Point(0, 1));
        double scaleX = (unitX - origin).Length;
        double scaleY = (unitY - origin).Length;
        return (scaleX, scaleY);
    }

    public static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
