using System.Globalization;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Tests;

public sealed class CodexMonetaryFormatterTests
{
    [Theory]
    [InlineData(false, false, null, false)]
    [InlineData(false, true, null, true)]
    [InlineData(false, false, "0", true)]
    [InlineData(false, false, "1", false)]
    [InlineData(true, false, "0", true)]
    [InlineData(true, false, "0.004", true)]
    [InlineData(true, false, "1", true)]
    public void CreditVisibilityFollowsTruthTable(
        bool hasCredits,
        bool unlimited,
        string? balanceText,
        bool expected)
    {
        decimal? balance = balanceText is null
            ? null
            : decimal.Parse(balanceText, CultureInfo.InvariantCulture);
        Assert.Equal(
            expected,
            CodexMonetaryFormatter.ShouldDisplayCredit(
                new(hasCredits, unlimited, balance)));
    }

    [Theory]
    [InlineData("0", "$0.00")]
    [InlineData("0.004", "<$0.01")]
    [InlineData("0.005", "$0.01")]
    [InlineData("1234567.89", "$1,234,567.89")]
    public void CreditFormattingIsInvariant(string input, string expected)
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string cultureName in new[] { "ja-JP", "en-US", "de-DE" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                decimal value = decimal.Parse(input, CultureInfo.InvariantCulture);
                Assert.Equal(
                    expected,
                    CodexMonetaryFormatter.FormatCreditValue(
                        new(true, false, value)));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void UnlimitedIgnoresBalanceAndIndividualLimitShowsLimitState()
    {
        Assert.Equal(
            "無制限",
            CodexMonetaryFormatter.FormatCreditValue(new(false, true, null)));
        var limit = new CodexIndividualLimitSnapshot(
            12,
            10,
            0,
            new DateTimeOffset(2026, 7, 27, 1, 2, 0, TimeSpan.Zero));

        Assert.Equal(
            "$12.00 / $10.00",
            CodexMonetaryFormatter.FormatIndividualLimitValue(limit));
        Assert.Equal(
            "0% 残り · 7/27 1:02 リセット · LIMIT",
            CodexMonetaryFormatter.FormatIndividualLimitSecondary(
                limit,
                TimeZoneInfo.Utc));
    }

    [Fact]
    public void InvalidCreditAndLimitWithoutResetUseSafeFallbacks()
    {
        Assert.False(CodexMonetaryFormatter.ShouldDisplayCredit(null));
        Assert.False(CodexMonetaryFormatter.ShouldDisplayCredit(new(true, false, -1)));
        Assert.Equal(
            string.Empty,
            CodexMonetaryFormatter.FormatCreditValue(new(true, false, null)));
        Assert.Equal(
            "80% 残り",
            CodexMonetaryFormatter.FormatIndividualLimitSecondary(
                new(2, 10, 80, null),
                TimeZoneInfo.Utc));
    }
}
