using System.Security;
using AiUsageMonitor.Claude.Cli;
using Microsoft.Win32;

namespace AiUsageMonitor.Claude.Windows.Process;

/// <summary>
/// Windowsのmanaged settings（file-based source、HKLM／HKCUのpolicy、server-managed settingsの保存file）の有無。
/// registryは<c>SOFTWARE\Policies\ClaudeCode</c>の値<c>Settings</c>があるかだけを見て、内容は読まない。
/// </summary>
public static class WindowsClaudeManagedSettings
{
    internal const string PolicyKeyPath = @"SOFTWARE\Policies\ClaudeCode";
    internal const string PolicyValueName = "Settings";

    public static ClaudeManagedSettingsGuard CreateGuard() =>
        ClaudeManagedSettingsGuard.ForWindows(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
            PolicyValuePresent);

    /// <summary>HKLMまたはHKCUにpolicy値があればtrue。registryを確認できない場合もtrue（fail-closed）。</summary>
    internal static bool PolicyValuePresent()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return HasValue(RegistryHive.LocalMachine) || HasValue(RegistryHive.CurrentUser);
    }

    private static bool HasValue(RegistryHive hive)
    {
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = root.OpenSubKey(PolicyKeyPath, writable: false);
            return key is not null && key.GetValueNames().Contains(PolicyValueName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }
}
