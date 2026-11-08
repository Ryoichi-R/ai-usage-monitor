using System.Text.Json;

namespace AiUsageMonitor.WorkStatus;

public enum StopCompletionCheck
{
    /// <summary>No other Stop hook can keep the main turn running; Stop may confirm completion.</summary>
    Eligible,
    /// <summary>Another blocking Stop hook exists, so Stop stays a candidate.</summary>
    OtherStopHook,
    /// <summary>Enabled plugins may contribute Stop hooks that cannot be inspected here.</summary>
    PluginsEnabled,
    /// <summary>Managed policy outside readable files (registry/MDM) may contribute hooks.</summary>
    ManagedPolicy,
    /// <summary>A settings file exists but could not be read or parsed.</summary>
    Unreadable,
}

/// <summary>
/// Decides at connection time whether a Claude Code Stop hook may be treated as a confirmed
/// completion. Only settings files are inspected; skill frontmatter, server-managed settings,
/// and settings added after this check remain outside it, so a later continuation must still
/// retract the completion (see WorkStatusModel).
/// </summary>
public static class StopHookInspector
{
    private static readonly string[] BridgeNames = ["ai-usage-work-status-bridge", "ai-usage-work-status-bridge.exe"];

    public static StopCompletionCheck InspectProject(string projectRoot, string userClaudeDirectory, bool windows) =>
        InspectProject(projectRoot, userClaudeDirectory,
            windows ? @"C:\Program Files\ClaudeCode" : "/Library/Application Support/ClaudeCode",
            () => ManagedPolicyOutsideFiles(windows));

    /// <param name="managedRoot">Directory holding managed-settings.json and managed-settings.d.</param>
    /// <param name="managedPolicyPresent">True when registry/MDM policy exists that files cannot show.</param>
    public static StopCompletionCheck InspectProject(string projectRoot, string userClaudeDirectory, string managedRoot,
        Func<bool> managedPolicyPresent)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ArgumentException.ThrowIfNullOrEmpty(userClaudeDirectory);
        ArgumentException.ThrowIfNullOrEmpty(managedRoot);
        ArgumentNullException.ThrowIfNull(managedPolicyPresent);
        if (managedPolicyPresent()) return StopCompletionCheck.ManagedPolicy;
        var paths = new List<string>
        {
            Path.Combine(userClaudeDirectory, "settings.json"),
            Path.Combine(projectRoot, ".claude", "settings.json"),
            Path.Combine(projectRoot, ".claude", "settings.local.json"),
            Path.Combine(managedRoot, "managed-settings.json"),
        };
        try
        {
            string dropIn = Path.Combine(managedRoot, "managed-settings.d");
            if (Directory.Exists(dropIn)) paths.AddRange(Directory.EnumerateFiles(dropIn, "*.json").Order(StringComparer.Ordinal));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return StopCompletionCheck.Unreadable; }
        var documents = new List<string?>();
        foreach (string path in paths)
        {
            try { documents.Add(File.ReadAllText(path)); }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return StopCompletionCheck.Unreadable; }
        }
        return Inspect(documents);
    }

    public static StopCompletionCheck Inspect(IEnumerable<string?> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        StopCompletionCheck result = StopCompletionCheck.Eligible;
        foreach (string? text in documents)
        {
            if (text is null) continue;
            StopCompletionCheck current;
            try
            {
                using var json = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                current = InspectDocument(json.RootElement);
            }
            catch (JsonException) { current = StopCompletionCheck.Unreadable; }
            // Unreadable outranks every finding; otherwise keep the first blocking reason.
            if (current == StopCompletionCheck.Unreadable) return current;
            if (result == StopCompletionCheck.Eligible) result = current;
        }
        return result;
    }

    private static StopCompletionCheck InspectDocument(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return StopCompletionCheck.Unreadable;
        if (root.TryGetProperty("hooks", out JsonElement hooks))
        {
            if (hooks.ValueKind != JsonValueKind.Object) return StopCompletionCheck.Unreadable;
            if (hooks.TryGetProperty("Stop", out JsonElement groups))
            {
                if (groups.ValueKind != JsonValueKind.Array) return StopCompletionCheck.Unreadable;
                foreach (JsonElement group in groups.EnumerateArray())
                {
                    if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("hooks", out JsonElement handlers) ||
                        handlers.ValueKind != JsonValueKind.Array) return StopCompletionCheck.Unreadable;
                    foreach (JsonElement handler in handlers.EnumerateArray())
                        if (MayBlock(handler)) return StopCompletionCheck.OtherStopHook;
                }
            }
        }
        if (root.TryGetProperty("enabledPlugins", out JsonElement plugins) && plugins.ValueKind == JsonValueKind.Object &&
            plugins.EnumerateObject().Any(plugin => plugin.Value.ValueKind != JsonValueKind.False))
            return StopCompletionCheck.PluginsEnabled;
        return StopCompletionCheck.Eligible;
    }

    private static bool MayBlock(JsonElement handler)
    {
        if (handler.ValueKind != JsonValueKind.Object) return true;
        if (Text(handler, "type") == "command" && Text(handler, "command") is { Length: > 0 } command &&
            BridgeNames.Contains(LeafName(command), StringComparer.OrdinalIgnoreCase)) return false;
        // Async hooks cannot block; asyncRewake can send Claude back to work on exit code 2.
        bool async = handler.TryGetProperty("async", out JsonElement value) && value.ValueKind == JsonValueKind.True;
        bool rewake = handler.TryGetProperty("asyncRewake", out JsonElement wake) && wake.ValueKind == JsonValueKind.True;
        return !async || rewake;
    }

    private static string LeafName(string command)
    {
        int index = command.LastIndexOfAny(['/', '\\']);
        return index >= 0 ? command[(index + 1)..] : command;
    }

    private static string Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out JsonElement item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    private static bool ManagedPolicyOutsideFiles(bool windows)
    {
        try
        {
            if (windows)
            {
                if (!OperatingSystem.IsWindows()) return false;
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\ClaudeCode");
                return key is not null;
            }
            string managed = "/Library/Managed Preferences";
            return File.Exists(Path.Combine(managed, "com.anthropic.claudecode.plist")) ||
                File.Exists(Path.Combine(managed, Environment.UserName, "com.anthropic.claudecode.plist"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return true;
        }
    }
}
