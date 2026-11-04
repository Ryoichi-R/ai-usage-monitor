using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiUsageMonitor.Core.WorkStatus;

namespace AiUsageMonitor.WorkStatus;

public static class HookNormalizer
{
    public static WorkStatusEvent? Normalize(JsonElement input, WorkProvider provider, bool desktopVerified,
        DateTimeOffset at, bool stopMayComplete = false)
    {
        if (!desktopVerified || !Enum.IsDefined(provider) || input.ValueKind != JsonValueKind.Object ||
            Text(input, "agent_id").Length > 0) return null;
        string session = Text(input, "session_id"), turn = Text(input, "turn_id");
        if (session.Length is 0 or > 160 || turn.Length > 160) return null;
        bool knownInactive = input.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.False;
        string hook = Text(input, "hook_event_name");
        WorkEventKind? kind = hook switch
        {
            "SessionStart" => WorkEventKind.Connected,
            "UserPromptSubmit" => WorkEventKind.Started,
            "PreToolUse" or "PostToolUse" => WorkEventKind.Activity,
            "PermissionRequest" => WorkEventKind.Waiting,
            "Stop" => stopMayComplete && knownInactive ? WorkEventKind.Completed : WorkEventKind.StopCandidate,
            "StopFailure" when provider == WorkProvider.ClaudeCode => WorkEventKind.Failed,
            "Interrupt" => WorkEventKind.Interrupted,
            "SessionEnd" => WorkEventKind.Ended,
            "Notification" when provider == WorkProvider.ClaudeCode => Text(input, "notification_type") switch
            {
                "permission_prompt" or "elicitation_dialog" or "elicitation_url_dialog" => WorkEventKind.Waiting,
                // idle_prompt and agent_completed do not prove the main turn completed.
                _ => null,
            },
            _ => null,
        };
        if (kind is null) return null;
        string[]? questions = null;
        if (provider == WorkProvider.CodexDesktop)
        {
            string tool = Text(input, "tool_name");
            if (tool.StartsWith("functions.", StringComparison.Ordinal)) tool = tool[10..];
            if (hook is "PreToolUse" or "PostToolUse" && tool is "request_user_input" or "request_user_input_async")
            {
                string call = Text(input, "tool_use_id");
                if (call.Length is 0 or > 240 || !input.TryGetProperty("tool_input", out var arguments) ||
                    arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("questions", out var items) ||
                    items.ValueKind != JsonValueKind.Array || items.GetArrayLength() is 0 or > 16) return null;
                questions = Enumerable.Range(0, items.GetArrayLength()).Select(index => QuestionId(call, index)).ToArray();
                if (hook == "PreToolUse") kind = WorkEventKind.QuestionOpened;
                else if (tool == "request_user_input") kind = WorkEventKind.QuestionAnswered;
                else if (input.TryGetProperty("tool_response", out var response) && response.ValueKind == JsonValueKind.Object &&
                    response.TryGetProperty("accepted", out var accepted) && accepted.ValueKind == JsonValueKind.False)
                    kind = WorkEventKind.QuestionAnswered;
                else return null; // An asynchronous tool result is not the user's answer.
            }
            else if (hook == "UserPromptSubmit") questions = AnsweredQuestions(Text(input, "prompt"));
        }
        return new(1, Guid.NewGuid().ToString("N"), provider, Digest(session), turn.Length > 0 ? Digest(turn) : "",
            kind.Value, at.ToUniversalTime(), true, at.UtcTicks, kind == WorkEventKind.Completed, questions);
    }

    private static string QuestionId(string call, int index) => Digest(call + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string[]? AnsweredQuestions(string prompt)
    {
        const string start = "<send_user_message_question_reply>", end = "</send_user_message_question_reply>";
        ReadOnlySpan<char> value = prompt.AsSpan().Trim();
        if (!value.StartsWith(start, StringComparison.Ordinal) || !value.EndsWith(end, StringComparison.Ordinal)) return null;
        try
        {
            using var replies = JsonDocument.Parse(value[start.Length..^end.Length].ToString());
            if (replies.RootElement.ValueKind != JsonValueKind.Array || replies.RootElement.GetArrayLength() > 16) return null;
            var ids = new List<string>();
            foreach (var reply in replies.RootElement.EnumerateArray())
            {
                if (reply.ValueKind != JsonValueKind.Object) return null;
                string item = Text(reply, "questionItemId");
                if (item.Length is 0 or > 512) return null;
                using var identity = JsonDocument.Parse(item);
                var parts = identity.RootElement;
                if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 3 ||
                    parts[0].ValueKind != JsonValueKind.String || parts[0].GetString() != "request_user_input_async" ||
                    parts[1].ValueKind != JsonValueKind.String || parts[1].GetString() is not { Length: > 0 and <= 240 } call ||
                    parts[2].ValueKind != JsonValueKind.Number || !parts[2].TryGetInt32(out int index) || index is < 0 or >= 16) return null;
                ids.Add(QuestionId(call, index));
            }
            return ids.Count > 0 ? ids.Distinct(StringComparer.Ordinal).ToArray() : null;
        }
        catch (JsonException) { return null; }
    }

    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
}
