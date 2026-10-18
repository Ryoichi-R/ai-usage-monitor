using System.Globalization;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Presentation;

public static class CodexMonetaryFormatter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static bool ShouldDisplayCredit(CodexCreditSnapshot? credit)
    {
        if (credit is null)
            return false;
        if (credit.IsUnlimited)
            return true;
        if (credit.Balance is not { } balance || balance < 0)
            return false;
        return credit.HasCredits || balance == 0;
    }

    public static string FormatCreditValue(CodexCreditSnapshot credit)
    {
        ArgumentNullException.ThrowIfNull(credit);
        if (credit.IsUnlimited)
            return "無制限";
        if (credit.Balance is not { } balance)
            return string.Empty;
        if (balance > 0 && balance < 0.005m)
            return "<$0.01";
        return "$" + balance.ToString("#,##0.00", Invariant);
    }

    public static string FormatIndividualLimitValue(CodexIndividualLimitSnapshot limit)
    {
        ArgumentNullException.ThrowIfNull(limit);
        return string.Create(
            Invariant,
            $"${limit.Used:#,##0.00} / ${limit.Limit:#,##0.00}");
    }

    public static string FormatIndividualLimitSecondary(
        CodexIndividualLimitSnapshot limit,
        TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(limit);
        ArgumentNullException.ThrowIfNull(timeZone);

        string state = limit.IsLimitReached ? " · LIMIT" : string.Empty;
        if (limit.ResetsAt is not { } reset)
            return $"{limit.RemainingPercent}% 残り{state}";

        DateTimeOffset local = TimeZoneInfo.ConvertTime(reset, timeZone);
        return string.Create(
            Invariant,
            $"{limit.RemainingPercent}% 残り · {local:M/d H:mm} リセット{state}");
    }
}
