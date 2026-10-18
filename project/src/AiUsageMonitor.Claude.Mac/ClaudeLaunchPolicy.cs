using System.Diagnostics;
using AiUsageMonitor.Claude.Cli;

namespace AiUsageMonitor.Claude.Mac;

public sealed class ClaudeLaunchPolicy(string home, string systemLibrary = "/Library")
{
    public string Home { get; } = home;
    internal static readonly string[] IsolationArguments =
        ["--setting-sources", "", "--tools", "", "--no-chrome", "--strict-mcp-config", "--safe-mode", "--ax-screen-reader"];

    private readonly ClaudeManagedSettingsGuard _managed = ClaudeManagedSettingsGuard.ForMacOS(home, systemLibrary);

    public bool AllowsLaunch() => _managed.AllowsLaunch();

    internal ProcessStartInfo CreateStartInfo(string executable, string workspace, string? probeArgument = null)
    {
        if (!AllowsLaunch()) throw new InvalidOperationException(ClaudeManagedSettingsGuard.ReasonCode);
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workspace, UseShellExecute = false };
        info.Environment.Clear();
        info.Environment["HOME"] = Home;
        info.Environment["USER"] = Path.GetFileName(Home);
        info.Environment["LOGNAME"] = Path.GetFileName(Home);
        info.Environment["TMPDIR"] = "/private/tmp/";
        info.Environment["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin";
        info.Environment["LANG"] = "en_US.UTF-8";
        info.Environment["TERM"] = "xterm-256color";
        // 利用者設定を読まないため、製品が起動したCLI自身が検証済みversionを自動更新しないよう公式変数で止める。
        info.Environment["DISABLE_AUTOUPDATER"] = "1";
        if (probeArgument is not null) info.ArgumentList.Add(probeArgument);
        else foreach (string argument in IsolationArguments) info.ArgumentList.Add(argument);
        return info;
    }
}

public sealed class MacClaudeExecutableLocator(string home, string helperPath, ClaudeLaunchPolicy policy) : IClaudeExecutableLocator
{
    public ClaudeExecutableInfo Resolve(string? configuredPath)
    {
        if (!policy.AllowsLaunch()) return new("", null, false, null, ClaudeManagedSettingsGuard.ReasonCode);
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
                info.ArgumentList.Add("--inspect-claude-v2"); info.ArgumentList.Add(path);
                using var process = Process.Start(info);
                if (process is null) return new(path, null, false, null, "SIGNATURE_VERIFICATION_FAILED");
                if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); return new(path, null, false, null, "SIGNATURE_TIMEOUT"); }
                if (process.ExitCode != 0) return new(path, null, false, null, "SIGNATURE_VERIFICATION_FAILED");
                string token = process.StandardOutput.ReadToEnd();
                if (token.Length > 600) return new(path, null, false, null, "CLI_IDENTITY_INVALID");
                return new(path, null, true, "Anthropic", null) { Identity = MacClaudeBinaryIdentity.Parse(token).Token };
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { return new(candidate, null, false, null, "SIGNATURE_VERIFICATION_FAILED"); }
        }
        return new("", null, false, null, "CLAUDE_NOT_INSTALLED");
    }
}
