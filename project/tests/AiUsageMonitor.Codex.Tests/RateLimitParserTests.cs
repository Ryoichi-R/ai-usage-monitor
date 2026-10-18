using System.Text.Json;
using AiUsageMonitor.Codex.Client;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Codex.Tests;

public sealed class RateLimitParserTests
{
    [Fact]
    public void PrefersCodexBucketAndMapsBothWindows()
    {
        long reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        using JsonDocument document = JsonDocument.Parse($$"""
        { "rateLimits": { "primary": { "usedPercent": 99, "windowDurationMins": 1 } },
          "rateLimitsByLimitId": { "codex": { "limitId": "codex", "limitName": "Codex", "planType": "plus", "credits": { "hasCredits": true, "unlimited": false, "balance": "12.5" }, "rateLimitReachedType": null,
            "primary": { "usedPercent": 25, "windowDurationMins": 300, "resetsAt": {{reset}} },
            "secondary": { "usedPercent": 50, "windowDurationMins": 10080, "resetsAt": {{reset}} } } } }
        """);
        UsageSnapshot result = CodexUsageClient.Parse(document.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal("plus", result.PlanType);
        Assert.Equal(12.5m, result.Credits);
        Assert.Collection(result.Windows, first => Assert.Equal(25, first.UsedPercent), second => Assert.Equal(50, second.UsedPercent));
    }

    [Fact]
    public void ParsesMonetaryComponentsFromTheSelectedBucketOnly()
    {
        long reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        using JsonDocument document = JsonDocument.Parse($$"""
        {
          "rateLimits": { "credits": { "hasCredits": true, "unlimited": false, "balance": "99.00" } },
          "rateLimitsByLimitId": {
            "codex": {
              "primary": { "usedPercent": 20 },
              "credits": { "hasCredits": true, "unlimited": false, "balance": "1.25" },
              "individualLimit": { "used": "2.50", "limit": "10.00", "remainingPercent": 75, "resetsAt": {{reset}} }
            }
          }
        }
        """);
        UsageSnapshot result = CodexUsageClient.Parse(document.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal(1.25m, result.CreditSnapshot?.Balance);
        Assert.Equal(2.50m, result.IndividualLimit?.Used);
        Assert.Equal(10m, result.IndividualLimit?.Limit);
        Assert.Equal(75, result.IndividualLimit?.RemainingPercent);
    }

    [Theory]
    [InlineData(" 1.0")]
    [InlineData("+1.0")]
    [InlineData("1e2")]
    [InlineData("1,000")]
    [InlineData("-1")]
    public void InvalidBalanceHidesOnlyCreditComponent(string balance)
    {
        using JsonDocument document = JsonDocument.Parse($$"""
        { "rateLimits": {
          "primary": { "usedPercent": 10 },
          "credits": { "hasCredits": true, "unlimited": false, "balance": "{{balance}}" }
        } }
        """);
        UsageSnapshot result = CodexUsageClient.Parse(document.RootElement, DateTimeOffset.UtcNow);
        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Single(result.Windows);
        Assert.Null(result.CreditSnapshot);
    }

    [Fact]
    public void MissingBucketIsUnsupported()
    {
        using JsonDocument document = JsonDocument.Parse("{}");
        Assert.Equal(UsageAvailability.Unsupported, CodexUsageClient.Parse(document.RootElement, DateTimeOffset.UtcNow).Availability);
    }

    [Fact]
    public void MillisecondLikeResetIsNotDisplayed()
    {
        using JsonDocument document = JsonDocument.Parse("""{"rateLimits":{"primary":{"usedPercent":1,"windowDurationMins":300,"resetsAt":9999999999999}}}""");
        Assert.Null(CodexUsageClient.Parse(document.RootElement, DateTimeOffset.UtcNow).Windows[0].ResetsAt);
    }

    [Fact]
    public void UnlimitedCreditWinsOverInvalidBalance()
    {
        using JsonDocument document = JsonDocument.Parse(
            """{"rateLimits":{"credits":{"hasCredits":false,"unlimited":true,"balance":"invalid"}}}""");

        UsageSnapshot result = CodexUsageClient.Parse(
            document.RootElement,
            DateTimeOffset.UtcNow);

        Assert.True(result.CreditSnapshot?.IsUnlimited);
        Assert.Null(result.CreditSnapshot?.Balance);
        Assert.Equal(UsageAvailability.Available, result.Availability);
    }

    [Fact]
    public void UsedAboveLimitForcesZeroRemainingAndKeepsCard()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {"rateLimits":{"individualLimit":{
              "used":"12.00","limit":"10.00","remainingPercent":50,"resetsAt":null
            }}}
            """);

        CodexIndividualLimitSnapshot limit = Assert.IsType<CodexIndividualLimitSnapshot>(
            CodexUsageClient.Parse(document.RootElement, DateTimeOffset.UtcNow).IndividualLimit);

        Assert.Equal(0, limit.RemainingPercent);
        Assert.True(limit.IsLimitReached);
    }

    [Fact]
    public void PreferredBucketDoesNotBorrowMissingMonetaryComponents()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {
              "rateLimits":{"credits":{"hasCredits":true,"unlimited":false,"balance":"99"}},
              "rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":10}}}
            }
            """);

        UsageSnapshot result = CodexUsageClient.Parse(
            document.RootElement,
            DateTimeOffset.UtcNow);

        Assert.Single(result.Windows);
        Assert.Null(result.CreditSnapshot);
    }

    [Fact]
    public void MonetaryComponentSurvivesUnsupportedWindows()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            {"rateLimits":{
              "credits":{"hasCredits":true,"unlimited":false,"balance":"1.00"}
            }}
            """);

        UsageSnapshot result = CodexUsageClient.Parse(
            document.RootElement,
            DateTimeOffset.UtcNow);

        Assert.Equal(UsageAvailability.Available, result.Availability);
        Assert.Equal(UsageAvailability.Unsupported, result.RateLimitAvailability);
        Assert.Equal("SCHEMA_UNSUPPORTED", result.RateLimitReason);
        Assert.Equal(1m, result.CreditSnapshot?.Balance);
    }
}
