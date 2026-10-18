using AiUsageMonitor.Claude.Cli;

namespace AiUsageMonitor.Claude.Cli.Tests;

public sealed class ClaudeScreenContractTests
{
    [Fact]
    public void FailureCodesRoundTripThroughExistingReasonStrings()
    {
        var expected = new Dictionary<ClaudeScreenFailureCode, string>
        {
            [ClaudeScreenFailureCode.HelperFailed] = "HELPER_FAILED",
            [ClaudeScreenFailureCode.HelperTimeout] = "HELPER_TIMEOUT",
            [ClaudeScreenFailureCode.HelperInvalidResponse] = "HELPER_INVALID_RESPONSE",
            [ClaudeScreenFailureCode.AttachFailed] = "ATTACH_FAILED",
            [ClaudeScreenFailureCode.ConInFailed] = "CONIN_FAILED",
            [ClaudeScreenFailureCode.ConOutFailed] = "CONOUT_FAILED",
            [ClaudeScreenFailureCode.HelperException] = "HELPER_EXCEPTION",
            [ClaudeScreenFailureCode.ScreenBoundsInvalid] = "SCREEN_BOUNDS_INVALID",
            [ClaudeScreenFailureCode.ScreenInfoFailed] = "SCREEN_INFO_FAILED",
            [ClaudeScreenFailureCode.ScreenReadFailed] = "SCREEN_READ_FAILED",
            [ClaudeScreenFailureCode.InputWriteFailed] = "INPUT_WRITE_FAILED",
            [ClaudeScreenFailureCode.InvalidArguments] = "INVALID_ARGUMENTS",
            [ClaudeScreenFailureCode.InvalidOperation] = "INVALID_OPERATION",
            [ClaudeScreenFailureCode.WorkspaceProvisionFailed] = "WORKSPACE_PROVISION_FAILED",
            [ClaudeScreenFailureCode.ProcessStartFailed] = "PROCESS_START_FAILED",
            [ClaudeScreenFailureCode.ProcessExited] = "PROCESS_EXITED",
            [ClaudeScreenFailureCode.ReadyTimeout] = "READY_TIMEOUT",
            [ClaudeScreenFailureCode.InputFailed] = "INPUT_FAILED",
            [ClaudeScreenFailureCode.ConsoleBufferReadFailed] = "CONSOLE_BUFFER_READ_FAILED",
            [ClaudeScreenFailureCode.UnexpectedUsageScreen] = "UNEXPECTED_USAGE_SCREEN",
            [ClaudeScreenFailureCode.UsageScreenParseFailed] = "USAGE_SCREEN_PARSE_FAILED",
            [ClaudeScreenFailureCode.DescendantEscaped] = "CLI_GROUP_ESCAPE_DETECTED",
            [ClaudeScreenFailureCode.ProcessCleanupFailed] = "PROCESS_CLEANUP_FAILED",
            [ClaudeScreenFailureCode.ClaudeNotInstalled] = "CLAUDE_NOT_INSTALLED",
            [ClaudeScreenFailureCode.ClaudeSignedOut] = "CLAUDE_SIGNED_OUT",
            [ClaudeScreenFailureCode.ClaudeTrustRequired] = "CLAUDE_TRUST_REQUIRED",
            [ClaudeScreenFailureCode.UnsupportedPlatform] = "UNSUPPORTED_PLATFORM",
        };

        Assert.Equal(Enum.GetValues<ClaudeScreenFailureCode>().Length, expected.Count);
        foreach ((ClaudeScreenFailureCode code, string reason) in expected)
        {
            Assert.Equal(reason, code.ToReasonCode());
            Assert.Equal(code, ClaudeScreenFailureCodeConvert.FromReasonCode(reason));
        }

        Assert.Equal(
            ClaudeScreenFailureCode.HelperInvalidResponse,
            ClaudeScreenFailureCodeConvert.FromReasonCode("UNKNOWN_REASON"));
        Assert.Equal(
            ClaudeScreenFailureCode.HelperInvalidResponse,
            ClaudeScreenFailureCodeConvert.FromReasonCode(null));
    }

    [Fact]
    public void SessionResultsPreserveSuccessValueAndFailureReason()
    {
        ScreenSessionResult operationSuccess = ScreenSessionResult.Ok();
        Assert.True(operationSuccess.Success);
        Assert.Null(operationSuccess.ReasonCode);

        ScreenSessionResult operationFailure = ScreenSessionResult.Fail(ClaudeScreenFailureCode.ReadyTimeout);
        Assert.False(operationFailure.Success);
        Assert.Equal(ClaudeScreenFailureCode.ReadyTimeout, operationFailure.ReasonCode);

        var snapshot = new ScreenSnapshot(["ready"], 400, 120);
        ScreenSessionResult<ScreenSnapshot> valueSuccess = ScreenSessionResult<ScreenSnapshot>.Ok(snapshot);
        Assert.True(valueSuccess.Success);
        Assert.Equal(snapshot, valueSuccess.Value);
        Assert.Null(valueSuccess.ReasonCode);

        ScreenSessionResult<ScreenSnapshot> valueFailure =
            ScreenSessionResult<ScreenSnapshot>.Fail(ClaudeScreenFailureCode.ScreenReadFailed);
        Assert.False(valueFailure.Success);
        Assert.Null(valueFailure.Value);
        Assert.Equal(ClaudeScreenFailureCode.ScreenReadFailed, valueFailure.ReasonCode);
    }

    [Fact]
    public void ExecutableInformationExposesTrustAndFailureDetails()
    {
        var info = new ClaudeExecutableInfo("/test/claude", "2.1.218", false, null, "UNTRUSTED_EXECUTABLE");

        Assert.Equal("/test/claude", info.Path);
        Assert.Equal("2.1.218", info.Version);
        Assert.False(info.SignatureValid);
        Assert.Null(info.Publisher);
        Assert.Equal("UNTRUSTED_EXECUTABLE", info.FailureReason);
    }
}
