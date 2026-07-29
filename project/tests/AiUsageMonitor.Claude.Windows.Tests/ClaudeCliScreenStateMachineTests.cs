using AiUsageMonitor.Claude.Windows.Cli;

namespace AiUsageMonitor.Claude.Windows.Tests;

public sealed class ClaudeCliScreenStateMachineTests
{
    private static string[] Fixture(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "claude-cli-screens", name));

    [Theory]
    [InlineData("trust-prompt-en.txt", ClaudeCliScreenSignature.TrustPrompt)]
    [InlineData("setup-screen-en.txt", ClaudeCliScreenSignature.SetupScreen)]
    [InlineData("signed-out-en.txt", ClaudeCliScreenSignature.SignedOut)]
    [InlineData("usage-screen-en.txt", ClaudeCliScreenSignature.UsageScreen)]
    [InlineData("usage-screen-ja.txt", ClaudeCliScreenSignature.UsageScreen)]
    [InlineData("ready-en.txt", ClaudeCliScreenSignature.Ready)]
    [InlineData("unknown.txt", ClaudeCliScreenSignature.Unknown)]
    public void ClassifiesKnownScreens(string fixture, ClaudeCliScreenSignature expected) =>
        Assert.Equal(expected, ClaudeCliScreenStateMachine.Classify(Fixture(fixture)));

    [Fact]
    public void TrustPromptWinsOverOtherAnchorsOnTheSameScreen()
    {
        string[] lines =
        [
            "Current session",
            "Is this a project you created or one you trust?",
            "? for shortcuts",
        ];
        Assert.Equal(ClaudeCliScreenSignature.TrustPrompt, ClaudeCliScreenStateMachine.Classify(lines));
    }

    [Fact]
    public void NullInputIsUnknown() =>
        Assert.Equal(ClaudeCliScreenSignature.Unknown, ClaudeCliScreenStateMachine.Classify(null));

    [Fact]
    public void EmptyInputIsUnknown() =>
        Assert.Equal(ClaudeCliScreenSignature.Unknown, ClaudeCliScreenStateMachine.Classify([]));

    [Fact]
    public void OversizedScreenIsUnknown()
    {
        string[] tooManyLines = Enumerable.Repeat("? for shortcuts", ClaudeCliScreenStateMachine.MaximumLines + 1).ToArray();
        Assert.Equal(ClaudeCliScreenSignature.Unknown, ClaudeCliScreenStateMachine.Classify(tooManyLines));

        string[] tooLongLine = [new string('x', ClaudeCliScreenStateMachine.MaximumLineLength + 1) + "? for shortcuts"];
        Assert.Equal(ClaudeCliScreenSignature.Unknown, ClaudeCliScreenStateMachine.Classify(tooLongLine));
    }

    [Fact]
    public void NullLineIsToleratedWithoutMatching() =>
        Assert.Equal(ClaudeCliScreenSignature.Unknown, ClaudeCliScreenStateMachine.Classify([null!, "   "]));

    [Fact]
    public void PromptBoxWithoutReadyAnchorIsUnknown() =>
        Assert.Equal(ClaudeCliScreenSignature.Unknown, ClaudeCliScreenStateMachine.Classify(["│ >  ", "╰────╯"]));

    [Fact]
    public void AccessibilityPromptRequiresItsExplicitModeAnchor() =>
        Assert.Equal(
            ClaudeCliScreenSignature.Ready,
            ClaudeCliScreenStateMachine.Classify(["[Screen Reader Mode: on via flag]", "$"]));

    [Theory]
    [InlineData(ClaudeCliScreenSignature.Ready, true)]
    [InlineData(ClaudeCliScreenSignature.UsageScreen, false)]
    [InlineData(ClaudeCliScreenSignature.TrustPrompt, false)]
    [InlineData(ClaudeCliScreenSignature.SetupScreen, false)]
    [InlineData(ClaudeCliScreenSignature.SignedOut, false)]
    [InlineData(ClaudeCliScreenSignature.Unknown, false)]
    public void OnlyReadyAllowsUsageCommand(ClaudeCliScreenSignature signature, bool expected) =>
        Assert.Equal(expected, ClaudeCliScreenStateMachine.AllowsUsageCommand(signature));

    [Theory]
    [InlineData(ClaudeCliScreenSignature.TrustPrompt, true)]
    [InlineData(ClaudeCliScreenSignature.SetupScreen, true)]
    [InlineData(ClaudeCliScreenSignature.Ready, false)]
    [InlineData(ClaudeCliScreenSignature.SignedOut, false)]
    [InlineData(ClaudeCliScreenSignature.Unknown, false)]
    public void SetupScreensRequireUserAction(ClaudeCliScreenSignature signature, bool expected) =>
        Assert.Equal(expected, ClaudeCliScreenStateMachine.RequiresUserSetup(signature));
}
