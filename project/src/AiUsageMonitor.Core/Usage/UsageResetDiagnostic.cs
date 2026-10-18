namespace AiUsageMonitor.Core.Usage;

/// <summary>
/// Reset解析に失敗した際の非機微な診断情報。
/// 生の画面行、使用率、account、絶対日時は保持しない。
/// </summary>
public sealed record UsageResetDiagnostic(
    string Category,
    string ZoneCategory,
    long? CandidateDeltaMinutes,
    int WindowDurationMinutes);
