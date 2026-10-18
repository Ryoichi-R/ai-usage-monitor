namespace AiUsageMonitor.Claude.Cli;

/// <summary>解決済みの行×桁screenテキスト。色・属性は保持しない。</summary>
public sealed record ScreenSnapshot(IReadOnlyList<string> Lines, int Width, int Height);

/// <summary>
/// IClaudeScreenSession操作の結果。例外を通常の失敗チャネルにせず、
/// 失敗時は必ずReasonCodeを持つ。
/// </summary>
public readonly struct ScreenSessionResult
{
    private ScreenSessionResult(bool success, ClaudeScreenFailureCode? reasonCode)
    {
        Success = success;
        ReasonCode = reasonCode;
    }

    public bool Success { get; }
    public ClaudeScreenFailureCode? ReasonCode { get; }

    public static ScreenSessionResult Ok() => new(true, null);
    public static ScreenSessionResult Fail(ClaudeScreenFailureCode reasonCode) => new(false, reasonCode);
}

/// <summary>値を伴うIClaudeScreenSession操作の結果。</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Result-style factory methods keep call sites readable; T is inferred from the value argument.")]
public readonly struct ScreenSessionResult<T>
{
    private ScreenSessionResult(bool success, T? value, ClaudeScreenFailureCode? reasonCode)
    {
        Success = success;
        Value = value;
        ReasonCode = reasonCode;
    }

    public bool Success { get; }
    public T? Value { get; }
    public ClaudeScreenFailureCode? ReasonCode { get; }

    public static ScreenSessionResult<T> Ok(T value) => new(true, value, null);
    public static ScreenSessionResult<T> Fail(ClaudeScreenFailureCode reasonCode) => new(false, default, reasonCode);
}
