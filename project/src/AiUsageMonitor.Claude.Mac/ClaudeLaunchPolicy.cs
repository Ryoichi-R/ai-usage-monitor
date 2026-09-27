using System.Diagnostics;
using AiUsageMonitor.Claude.Cli;

namespace AiUsageMonitor.Claude.Mac;

public sealed class ClaudeLaunchPolicy(string home, string systemLibrary = "/Library")
{
    public const string VerifiedVersion = "2.1.274";
    internal static readonly string[] IsolationArguments =
        ["--setting-sources", "", "--tools", "", "--no-chrome", "--strict-mcp-config", "--safe-mode", "--ax-screen-reader"];

    public bool AllowsLaunch()
    {
        string[] paths = [
            Path.Combine(home, ".claude", "remote-settings.json"),
            Path.Combine(systemLibrary, "Application Support", "ClaudeCode", "managed-settings.json"),
            Path.Combine(systemLibrary, "Application Support", "ClaudeCode", "managed-settings.d"),
            Path.Combine(systemLibrary, "Application Support", "ClaudeCode", "managed-mcp.json"),
            Path.Combine(systemLibrary, "Managed Preferences", "com.anthropic.claudecode.plist"),
            Path.Combine(systemLibrary, "Managed Preferences", Path.GetFileName(home), "com.anthropic.claudecode.plist"),
            Path.Combine(home, "Library", "Managed Preferences", "com.anthropic.claudecode.plist")];
        foreach (string path in paths)
        {
            try { _ = File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
        return true;
    }

    internal ProcessStartInfo CreateStartInfo(string executable, string workspace, string? probeArgument = null)
    {
        if (!AllowsLaunch()) throw new InvalidOperationException("MANAGED_SETTINGS_PRESENT");
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workspace, UseShellExecute = false };
        info.Environment.Clear();
        info.Environment["HOME"] = home;
        info.Environment["USER"] = Path.GetFileName(home);
        info.Environment["LOGNAME"] = Path.GetFileName(home);
        info.Environment["TMPDIR"] = "/private/tmp/";
        info.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        info.Environment["LANG"] = "en_US.UTF-8";
        info.Environment["TERM"] = "xterm-256color";
        if (probeArgument is not null) info.ArgumentList.Add(probeArgument);
        else foreach (string argument in IsolationArguments) info.ArgumentList.Add(argument);
        return info;
    }
}

public sealed class MacClaudeExecutableLocator(string home, string helperPath, ClaudeLaunchPolicy policy) : IClaudeExecutableLocator
{
    public ClaudeExecutableInfo Resolve(string? configuredPath)
    {
        if (!policy.AllowsLaunch()) return new("", null, false, null, "MANAGED_SETTINGS_PRESENT");
        string[] candidates = string.IsNullOrWhiteSpace(configuredPath)
            ? [Path.Combine(home, ".local", "bin", "claude"), "/opt/homebrew/bin/claude", "/usr/local/bin/claude"]
            : [configuredPath];
        foreach (string candidate in candidates)
        {
            if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate)) continue;
            try
            {
                string path = File.ResolveLinkTarget(candidate, true)?.FullName ?? Path.GetFullPath(candidate);
                var info = new ProcessStartInfo(helperPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("--verify-claude"); info.ArgumentList.Add(path);
                using var process = Process.Start(info);
                if (process is null) return new(path, null, false, null, "SIGNATURE_VERIFICATION_FAILED");
                if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); return new(path, null, false, null, "SIGNATURE_TIMEOUT"); }
                return process.ExitCode == 0
                    ? new(path, null, true, "Anthropic", null)
                    : new(path, null, false, null, "SIGNATURE_VERIFICATION_FAILED");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { return new(candidate, null, false, null, "SIGNATURE_VERIFICATION_FAILED"); }
        }
        return new("", null, false, null, "CLAUDE_NOT_INSTALLED");
    }
}
