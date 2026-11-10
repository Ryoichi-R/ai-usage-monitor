namespace AiUsageMonitor.Core.Usage;

public static class ClaudeUsageFailure
{
    public const string ScreenParseFailed = "USAGE_SCREEN_PARSE_FAILED";
    public const string SectionAmbiguous = "USAGE_SCREEN_SECTION_AMBIGUOUS";

    public static bool IsReadingFailure(string? reason) => reason is
        ScreenParseFailed or SectionAmbiguous or
        "USAGE_RESET_FORMAT_UNSUPPORTED" or "USAGE_RESET_AMBIGUOUS" or
        "USAGE_RESET_OUT_OF_RANGE" or "USAGE_RESET_TIME_ZONE_UNSUPPORTED" or
        "USAGE_RESET_REGRESSION_REJECTED";

    public static bool IsTimeout(string? reason) =>
        reason is "READY_TIMEOUT" or "CLI_CONFIRMATION_TIMEOUT";

    public static bool RetainOnRetry(string? reason) =>
        IsReadingFailure(reason) || IsTimeout(reason) || reason == "CONSOLE_BUFFER_READ_FAILED";
}
