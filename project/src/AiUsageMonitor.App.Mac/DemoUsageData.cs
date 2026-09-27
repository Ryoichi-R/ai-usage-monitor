using AiUsageMonitor.App.UI.ViewModels;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.App.Mac;

/// <summary>P0-2の表示確認用の固定データ。実際の利用状況ではない。</summary>
internal static class DemoUsageData
{
    public static UsageViewModel Create()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var viewModel = new UsageViewModel(() => now) { ShowCodex = true, ShowClaude = true };
        viewModel.Apply(Snapshot(UsageProvider.Codex, now, fiveHourUsed: 38, weeklyUsed: 82));
        viewModel.Apply(Snapshot(UsageProvider.Claude, now, fiveHourUsed: 12, weeklyUsed: 95));
        return viewModel;
    }

    private static UsageSnapshot Snapshot(UsageProvider provider, DateTimeOffset now, double fiveHourUsed, double weeklyUsed) => new(
        provider,
        now,
        now,
        UsageAvailability.Available,
        Reason: null,
        PlanType: null,
        Windows:
        [
            new UsageWindowSnapshot(null, null, "five_hour", fiveHourUsed, UsageWindowPolicy.FiveHourDurationMinutes, now.AddHours(3).AddMinutes(12), null),
            new UsageWindowSnapshot(null, null, "seven_day", weeklyUsed, UsageWindowPolicy.SevenDayDurationMinutes, now.AddDays(4).AddHours(2), null),
        ],
        Credits: null,
        IsStale: false,
        LastSuccessfulAt: now);
}
