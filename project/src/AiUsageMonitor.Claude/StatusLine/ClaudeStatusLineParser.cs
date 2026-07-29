using System.Text;
using System.Text.Json;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Claude.StatusLine;

public static class ClaudeStatusLineParser
{
    public const int MaximumPayloadBytes = 16 * 1024;
    public const int ProtocolVersion = 1;

    public static UsageSnapshot Parse(ReadOnlySpan<byte> utf8, DateTimeOffset receivedAt)
    {
        if (utf8.Length == 0 || utf8.Length > MaximumPayloadBytes)
            return Failure(receivedAt, utf8.Length == 0 ? "EMPTY_PAYLOAD" : "PAYLOAD_TOO_LARGE");
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8.ToArray());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Failure(receivedAt, "INVALID_ROOT");
            if (root.TryGetProperty("protocol", out JsonElement protocol) &&
                (!protocol.TryGetInt32(out int value) || value != ProtocolVersion))
                return Failure(receivedAt, "PROTOCOL_MISMATCH");

            string? version = GetString(root, "version");
            if (!root.TryGetProperty("rate_limits", out JsonElement limits) || limits.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return new(UsageProvider.Claude, receivedAt, receivedAt, UsageAvailability.Waiting, "RATE_LIMITS_MISSING", null, [], null, false, null, version);
            if (limits.ValueKind != JsonValueKind.Object) return Failure(receivedAt, "INVALID_RATE_LIMITS", version);

            var windows = new List<UsageWindowSnapshot>(2);
            string? error = AddWindow(
                    limits,
                    "five_hour",
                    "five_hour",
                    UsageWindowPolicy.FiveHourDurationMinutes,
                    receivedAt,
                    windows)
                ?? AddWindow(
                    limits,
                    "seven_day",
                    "seven_day",
                    UsageWindowPolicy.SevenDayDurationMinutes,
                    receivedAt,
                    windows);
            if (error is not null) return Failure(receivedAt, error, version);
            return windows.Count == 0
                ? new(UsageProvider.Claude, receivedAt, receivedAt, UsageAvailability.Waiting, "RATE_LIMITS_EMPTY", null, [], null, false, null, version)
                : new(UsageProvider.Claude, receivedAt, receivedAt, UsageAvailability.Available, null, null, windows, null, false, receivedAt, version);
        }
        catch (JsonException) { return Failure(receivedAt, "MALFORMED_JSON"); }
    }

    public static UsageSnapshot Parse(string json, DateTimeOffset receivedAt) =>
        Parse(Encoding.UTF8.GetBytes(json), receivedAt);

    private static string? AddWindow(JsonElement limits, string property, string slot, int duration, DateTimeOffset now, List<UsageWindowSnapshot> output)
    {
        if (!limits.TryGetProperty(property, out JsonElement window) || window.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (window.ValueKind != JsonValueKind.Object) return "INVALID_WINDOW";
        if (!window.TryGetProperty("used_percentage", out JsonElement usedElement) ||
            usedElement.ValueKind != JsonValueKind.Number ||
            !usedElement.TryGetDouble(out double used) || !double.IsFinite(used) ||
            used < 0 || used > 100) return "INVALID_PERCENTAGE";
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resets_at", out JsonElement resetElement) && resetElement.ValueKind != JsonValueKind.Null)
        {
            if (!resetElement.TryGetInt64(out long epoch)) return "INVALID_RESET";
            try
            {
                DateTimeOffset candidate = DateTimeOffset.FromUnixTimeSeconds(epoch);
                if (candidate <= now || candidate > now.AddYears(1)) return "RESET_OUT_OF_RANGE";
                reset = candidate;
            }
            catch (ArgumentOutOfRangeException) { return "RESET_OUT_OF_RANGE"; }
        }
        output.Add(new(null, null, slot, used, duration, reset, null));
        return null;
    }

    private static string? GetString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static UsageSnapshot Failure(DateTimeOffset now, string reason, string? version = null) =>
        new(UsageProvider.Claude, now, now, UsageAvailability.Error, reason, null, [], null, false, null, version);
}
