namespace AiUsageMonitor.App.UI.Onboarding;

/// <summary>
/// 専用作業フォルダーでClaude Codeを起動し、利用者自身が信頼確認を行うためのコマンドを組み立てる。
/// 引数は製品のactive取得と同じ隔離引数。WindowsはPowerShell、macOS等はPOSIX shell向けに引用する。
/// macOSのactive取得は検証済みversionに限るため、信頼確認の対話起動でCLIが自動更新しないよう
/// 公式の<c>DISABLE_AUTOUPDATER=1</c>をそのコマンドだけに付ける。
/// </summary>
public static class ClaudeTrustCommand
{
    public const string IsolationArguments =
        "--setting-sources '' --tools '' --no-chrome --strict-mcp-config --safe-mode --ax-screen-reader";

    public static string Create(string workspaceFolder, string? executablePath, bool forPowerShell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceFolder);
        string command = string.IsNullOrWhiteSpace(executablePath) ? "claude" : executablePath;
        return forPowerShell
            ? $"Set-Location -LiteralPath '{EscapePowerShell(workspaceFolder)}'; & '{EscapePowerShell(command)}' {IsolationArguments}"
            : $"cd {QuotePosix(workspaceFolder)} && DISABLE_AUTOUPDATER=1 {QuotePosix(command)} {IsolationArguments}";
    }

    public static string CreateForCurrentPlatform(string workspaceFolder, string? executablePath) =>
        Create(workspaceFolder, executablePath, OperatingSystem.IsWindows());

    private static string EscapePowerShell(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    // POSIX shellでは単一引用符の中に単一引用符を書けないため、'\'' で閉じて再開する。
    private static string QuotePosix(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
