namespace AiUsageMonitor.Platform;

/// <summary>
/// OSログイン時の自動起動設定。Windows実装はレジストリRunキー、
/// macOS実装は~/Library/LaunchAgents/*.plist（またはSMAppService）を使う。
/// </summary>
public interface IStartupService
{
    void Apply(bool startWithSystem);
}
