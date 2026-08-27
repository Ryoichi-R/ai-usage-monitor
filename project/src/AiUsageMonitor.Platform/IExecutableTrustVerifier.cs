namespace AiUsageMonitor.Platform;

public readonly record struct ExecutableTrustResult(bool Valid, string? Publisher, string? FailureReason);

/// <summary>
/// 実行ファイルの署名を検証する抽象。Windows実装はWinVerifyTrust、
/// macOS実装はcodesignまたはSecurity.frameworkでanchor/identifier/chain条件を検証する。
/// 検証異常・parse不能・path identity不一致はすべてValid=falseのfail-closedとする。
/// </summary>
public interface IExecutableTrustVerifier
{
    ExecutableTrustResult Verify(string path);
}
