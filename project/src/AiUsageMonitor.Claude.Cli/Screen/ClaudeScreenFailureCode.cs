namespace AiUsageMonitor.Claude.Cli;

/// <summary>
/// IClaudeScreenSessionおよびClaudeCliActiveSourceが扱う失敗理由の閉じた分類。
/// Windows/macOS実装はこのcodeへ正規化し、ClaudeCliActiveSourceが既存のUsageAvailabilityへ
/// 決定的に写像する。未知・欠落・不正DTOはHelperInvalidResponse相当としてfail-closedする。
/// </summary>
public enum ClaudeScreenFailureCode
{
    // transport / helper
    HelperFailed,
    HelperTimeout,
    HelperInvalidResponse,
    AttachFailed,
    ConInFailed,
    ConOutFailed,
    HelperException,

    // screen / input
    ScreenBoundsInvalid,
    ScreenInfoFailed,
    ScreenReadFailed,
    InputWriteFailed,
    InvalidArguments,
    InvalidOperation,

    // acquisition mapping
    WorkspaceProvisionFailed,
    ProcessStartFailed,
    ProcessExited,
    ReadyTimeout,
    InputFailed,
    ConsoleBufferReadFailed,
    UnexpectedUsageScreen,
    UsageScreenParseFailed,

    // availability
    ClaudeNotInstalled,
    ClaudeSignedOut,
    ClaudeTrustRequired,
    UnsupportedPlatform,
}

/// <summary>
/// ClaudeScreenFailureCodeと、既存wire契約・UsageSnapshot.Reasonが使うreason文字列との相互変換。
/// Phase 1は既存wire reasonとの互換を維持する契約のため、この対応表がADR 003追記と契約テストの正本になる。
/// </summary>
public static class ClaudeScreenFailureCodeConvert
{
    public static string ToReasonCode(this ClaudeScreenFailureCode code) => code switch
    {
        ClaudeScreenFailureCode.HelperFailed => "HELPER_FAILED",
        ClaudeScreenFailureCode.HelperTimeout => "HELPER_TIMEOUT",
        ClaudeScreenFailureCode.HelperInvalidResponse => "HELPER_INVALID_RESPONSE",
        ClaudeScreenFailureCode.AttachFailed => "ATTACH_FAILED",
        ClaudeScreenFailureCode.ConInFailed => "CONIN_FAILED",
        ClaudeScreenFailureCode.ConOutFailed => "CONOUT_FAILED",
        ClaudeScreenFailureCode.HelperException => "HELPER_EXCEPTION",
        ClaudeScreenFailureCode.ScreenBoundsInvalid => "SCREEN_BOUNDS_INVALID",
        ClaudeScreenFailureCode.ScreenInfoFailed => "SCREEN_INFO_FAILED",
        ClaudeScreenFailureCode.ScreenReadFailed => "SCREEN_READ_FAILED",
        ClaudeScreenFailureCode.InputWriteFailed => "INPUT_WRITE_FAILED",
        ClaudeScreenFailureCode.InvalidArguments => "INVALID_ARGUMENTS",
        ClaudeScreenFailureCode.InvalidOperation => "INVALID_OPERATION",
        ClaudeScreenFailureCode.WorkspaceProvisionFailed => "WORKSPACE_PROVISION_FAILED",
        ClaudeScreenFailureCode.ProcessStartFailed => "PROCESS_START_FAILED",
        ClaudeScreenFailureCode.ProcessExited => "PROCESS_EXITED",
        ClaudeScreenFailureCode.ReadyTimeout => "READY_TIMEOUT",
        ClaudeScreenFailureCode.InputFailed => "INPUT_FAILED",
        ClaudeScreenFailureCode.ConsoleBufferReadFailed => "CONSOLE_BUFFER_READ_FAILED",
        ClaudeScreenFailureCode.UnexpectedUsageScreen => "UNEXPECTED_USAGE_SCREEN",
        ClaudeScreenFailureCode.UsageScreenParseFailed => "USAGE_SCREEN_PARSE_FAILED",
        ClaudeScreenFailureCode.ClaudeNotInstalled => "CLAUDE_NOT_INSTALLED",
        ClaudeScreenFailureCode.ClaudeSignedOut => "CLAUDE_SIGNED_OUT",
        ClaudeScreenFailureCode.ClaudeTrustRequired => "CLAUDE_TRUST_REQUIRED",
        ClaudeScreenFailureCode.UnsupportedPlatform => "UNSUPPORTED_PLATFORM",
        _ => "HELPER_INVALID_RESPONSE",
    };

    private static readonly Dictionary<string, ClaudeScreenFailureCode> ByReasonCode =
        Enum.GetValues<ClaudeScreenFailureCode>().ToDictionary(code => code.ToReasonCode(), code => code);

    /// <summary>
    /// wire reason文字列をcodeへ写像する。未知・欠落reasonはHelperInvalidResponseへfail-closedする。
    /// </summary>
    public static ClaudeScreenFailureCode FromReasonCode(string? reason) =>
        reason is not null && ByReasonCode.TryGetValue(reason, out ClaudeScreenFailureCode code)
            ? code
            : ClaudeScreenFailureCode.HelperInvalidResponse;
}
