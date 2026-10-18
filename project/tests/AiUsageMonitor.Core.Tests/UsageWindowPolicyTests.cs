using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Core.Tests;

public sealed class UsageWindowPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void KnownWindowsUseOneSharedContract()
    {
        Assert.True(UsageWindowPolicy.TryGetDuration(300, out TimeSpan fiveHours));
        Assert.Equal(TimeSpan.FromHours(5), fiveHours);
        Assert.True(UsageWindowPolicy.TryGetDuration(10080, out TimeSpan sevenDays));
        Assert.Equal(TimeSpan.FromDays(7), sevenDays);
        Assert.False(UsageWindowPolicy.TryGetDuration(60, out _));
    }

    [Fact]
    public void ResetValidationIsStrictAtLowerBoundAndTolerantAtUpperBound()
    {
        Assert.False(UsageWindowPolicy.IsValidReset(Now, Now, 300));
        Assert.False(UsageWindowPolicy.IsValidReset(Now.AddMinutes(-1), Now, 300));
        Assert.True(UsageWindowPolicy.IsValidReset(Now.AddHours(5).AddMinutes(2), Now, 300));
        Assert.False(UsageWindowPolicy.IsValidReset(
            Now.AddHours(5).AddMinutes(2).AddSeconds(1),
            Now,
            300));
    }
}
