using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.WorkStatus;

namespace AiUsageMonitor.App.UI.Tests;

public sealed class WorkStatusSetupCompletionTests
{
    [Fact]
    public void EveryCompletionCheckHasADistinctExplanation()
    {
        string[] texts = [.. Enum.GetValues<StopCompletionCheck>().Select(WorkStatusSetupWindow.DescribeCompletion)];
        Assert.All(texts, text => Assert.False(string.IsNullOrWhiteSpace(text)));
        Assert.Equal(texts.Length, texts.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("確定完了", WorkStatusSetupWindow.DescribeCompletion(StopCompletionCheck.Eligible));
        Assert.All(Enum.GetValues<StopCompletionCheck>().Where(check => check != StopCompletionCheck.Eligible),
            check => Assert.Contains("停止候補", WorkStatusSetupWindow.DescribeCompletion(check)));
    }

    [Fact]
    public void UserClaudeDirectoryFollowsConfiguredAbsolutePathOnly()
    {
        string? saved = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            string configured = Path.Combine(Path.GetTempPath(), "claude-config-test");
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", configured);
            Assert.Equal(configured, WorkStatusSetupWindow.DefaultUserClaudeDirectory());
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "relative");
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
                WorkStatusSetupWindow.DefaultUserClaudeDirectory());
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", saved); }
    }
}
