using System.Text;
using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus;

public static class HookExamples
{
    public static string Create(WorkProvider provider, string bridgePath, string stateRoot,
        bool windows, Version? claudeVersion = null)
    {
        bool Absolute(string value) => Path.IsPathFullyQualified(value) || !windows && value.StartsWith('/');
        if (!Enum.IsDefined(provider) || !Absolute(bridgePath) || !Absolute(stateRoot))
            throw new ArgumentException("WORK_STATUS_CONNECTION_PATH_INVALID");
        if (provider == WorkProvider.ClaudeCode && (claudeVersion is null || claudeVersion < new Version(2, 1, 295)))
            throw new ArgumentException("WORK_STATUS_CLAUDE_VERSION_UNVERIFIED");
        string[] names = provider == WorkProvider.CodexDesktop
            ? ["SessionStart", "UserPromptSubmit", "PreModelRequest", "PreToolUse", "PostToolUse", "PermissionRequest", "Stop", "Interrupt", "SessionEnd"]
            : ["SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse", "PostToolUseFailure", "PermissionRequest", "Stop", "StopFailure", "SessionEnd", "Notification"];
        var hooks = new Dictionary<string, object>();
        foreach (string name in names)
        {
            Dictionary<string, object> handler = provider == WorkProvider.ClaudeCode
                ? new() { ["type"] = "command", ["command"] = bridgePath, ["args"] = new[] { provider.ToString(), stateRoot }, ["timeout"] = 3, ["onFailure"] = "continue" }
                : new() { ["type"] = "command", ["command"] = CodexCommand(bridgePath, stateRoot, windows), ["timeout"] = 3 };
            var group = new Dictionary<string, object> { ["hooks"] = new[] { handler } };
            if (name == "Notification") group["matcher"] = "permission_prompt|idle_prompt|elicitation_dialog|elicitation_url_dialog";
            if (name == "PostToolUseFailure") group["matcher"] = "AskUserQuestion";
            hooks[name] = new[] { group };
        }
        return JsonSerializer.Serialize(new { hooks }, WorkStatusStore.JsonOptions);
    }

    private static string CodexCommand(string bridge, string root, bool windows)
    {
        if (!windows) return "exec " + ShellQuote(bridge) + " CodexDesktop " + ShellQuote(root);
        // The Windows inbox PowerShell avoids a dependency on pwsh. Encoding prevents
        // an outer shell from interpolating arguments. Claude uses native exec form.
        string command = "& '" + bridge.Replace("'", "''", StringComparison.Ordinal) + "' 'CodexDesktop' '" +
            root.Replace("'", "''", StringComparison.Ordinal) + "'; exit 0";
        return "powershell.exe -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
