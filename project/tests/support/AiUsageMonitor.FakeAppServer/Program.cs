using System.Text.Json;

if (args.Length != 1 || args[0] != "app-server")
    return 2;

string? codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
bool accountB = codexHome?.EndsWith("account-b", StringComparison.OrdinalIgnoreCase) == true;
bool duplicateIdentity = codexHome?.Contains(
    "duplicate-identity",
    StringComparison.OrdinalIgnoreCase) == true;
bool nullIdentity = codexHome?.Contains(
    "null-identity",
    StringComparison.OrdinalIgnoreCase) == true;
bool signedOutAfterSuccess = codexHome?.Contains(
    "signed-out-after-success",
    StringComparison.OrdinalIgnoreCase) == true;
bool signedOut = !signedOutAfterSuccess && codexHome?.Contains(
    "signed-out",
    StringComparison.OrdinalIgnoreCase) == true;
bool staleAfterSuccess = codexHome?.Contains(
    "stale-after-success",
    StringComparison.OrdinalIgnoreCase) == true;
int rateLimitReadCount = 0;
int accountReadCount = 0;
double usedPercent = accountB ? 70 : 25;
string balance = accountB ? "22.50" : "11.25";
string? email = nullIdentity
    ? null
    : duplicateIdentity
        ? "duplicate@example.test"
        : accountB
            ? "account-b@example.test"
            : "account-a@example.test";

while (await Console.In.ReadLineAsync() is { } line)
{
    using JsonDocument document = JsonDocument.Parse(line);
    JsonElement root = document.RootElement;
    if (root.TryGetProperty("id", out JsonElement id))
    {
        string? method = root.TryGetProperty("method", out JsonElement methodElement)
            ? methodElement.GetString()
            : null;
        if (method == "account/rateLimits/read" &&
            staleAfterSuccess &&
            ++rateLimitReadCount > 1)
            return 3;
        if (method == "account/read" &&
            signedOutAfterSuccess &&
            ++accountReadCount > 2)
            return 3;
        bool currentSignedOut = signedOut ||
            (signedOutAfterSuccess && accountReadCount == 2);
        object result = method switch
        {
            "account/read" => new
            {
                account = currentSignedOut
                    ? new { type = "apiKey", email = (string?)null }
                    : new { type = "chatgpt", email },
            },
            "account/rateLimits/read" => new
            {
                rateLimits = new
                {
                    planType = "plus",
                    primary = new
                    {
                        usedPercent,
                        windowDurationMins = 300,
                        resetsAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                    },
                    credits = new
                    {
                        hasCredits = true,
                        unlimited = false,
                        balance,
                    },
                    individualLimit = new
                    {
                        used = accountB ? "7.00" : "2.50",
                        limit = "10.00",
                        remainingPercent = accountB ? 30 : 75,
                        resetsAt = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
                    },
                },
            },
            _ => new { },
        };
        await Console.Out.WriteLineAsync(
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = id.GetInt64(), result }));
        if (method == "account/rateLimits/read")
        {
            await Console.Out.WriteLineAsync(
                JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    method = "account/rateLimits/updated",
                    @params = new { },
                }));
        }
        await Console.Out.FlushAsync();
    }
}

return 0;
