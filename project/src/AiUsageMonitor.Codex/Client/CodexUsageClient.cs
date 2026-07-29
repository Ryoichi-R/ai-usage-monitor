using System.Globalization;
using System.Text.Json;
using AiUsageMonitor.Codex.Process;
using AiUsageMonitor.Core.Usage;

namespace AiUsageMonitor.Codex.Client;

public sealed class CodexUsageClient : IAsyncDisposable
{
    private readonly CodexClientConfiguration _configuration;
    private CodexAppServerProcess? _server;
    public event Action? RateLimitsUpdated;

    public CodexUsageClient(CodexClientConfiguration configuration) =>
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

    public async Task<UsageSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        (await ReadAccountAsync(cancellationToken).ConfigureAwait(false)).Snapshot;

    internal Task<CodexAccountReadResult> ReadAccountAsync(CancellationToken cancellationToken) =>
        ReadCoreAsync(cancellationToken);

    private async Task<CodexAccountReadResult> ReadCoreAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string? executable = CodexExecutableLocator.Locate(_configuration.ExecutablePath);
        if (executable is null)
        {
            await ResetAsync().ConfigureAwait(false);
            return Result(Unavailable(now, UsageAvailability.NotInstalled, "CODEX_NOT_FOUND"));
        }
        if (_configuration.CodexHomePath is not null &&
            (!Path.IsPathFullyQualified(_configuration.CodexHomePath) ||
             !Directory.Exists(_configuration.CodexHomePath)))
        {
            await ResetAsync().ConfigureAwait(false);
            return Result(Unavailable(now, UsageAvailability.Unavailable, "CODEX_HOME_UNAVAILABLE"));
        }

        var identity = new AccountIdentity(null);
        try
        {
            if (_server is null)
            {
                _server = new CodexAppServerProcess();
                await _server.StartAsync(
                    executable,
                    _configuration.CodexHomePath,
                    _configuration.StartupTimeout,
                    _configuration.ProcessLifetimeGuardFactory,
                    cancellationToken).ConfigureAwait(false);
                _server.Connection.Notification += OnNotification;
            }
            JsonElement account = await _server.Connection.RequestAsync("account/read", new { refreshToken = false }, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            if (!TryReadChatGptIdentity(account, out identity))
                return Result(Unavailable(now, UsageAvailability.SignedOut, "SIGNED_OUT"));
            JsonElement response = await _server.Connection.RequestAsync("account/rateLimits/read", null, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            return new(Parse(response, now), identity);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await ResetAsync().ConfigureAwait(false);
            return Result(Unavailable(now, UsageAvailability.Unavailable, "RPC_TIMEOUT"), identity);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                InvalidOperationException or
                JsonException or
                EndOfStreamException or
                System.ComponentModel.Win32Exception)
        {
            await ResetAsync().ConfigureAwait(false);
            return Result(Unavailable(now, UsageAvailability.Unavailable, "RPC_FAILURE"), identity);
        }
    }

    private static bool TryReadChatGptIdentity(
        JsonElement response,
        out AccountIdentity identity)
    {
        identity = new(null);
        if (!response.TryGetProperty("account", out JsonElement account) ||
            account.ValueKind != JsonValueKind.Object ||
            !account.TryGetProperty("type", out JsonElement type) ||
            !string.Equals(type.GetString(), "chatgpt", StringComparison.OrdinalIgnoreCase))
            return false;

        string? email = GetString(account, "email");
        identity = new(string.IsNullOrWhiteSpace(email) ? null : email.Trim());
        return true;
    }

    public static UsageSnapshot Parse(JsonElement response, DateTimeOffset receivedAt)
    {
        JsonElement bucket;
        if (response.TryGetProperty("rateLimitsByLimitId", out JsonElement byId) && byId.ValueKind == JsonValueKind.Object && byId.TryGetProperty("codex", out JsonElement codex)) bucket = codex;
        else if (response.TryGetProperty("rateLimits", out JsonElement legacy)) bucket = legacy;
        else return new(UsageProvider.Codex, receivedAt, receivedAt, UsageAvailability.Unsupported, "METHOD_UNSUPPORTED", null, [], null, false, null);

        string? limitId = GetString(bucket, "limitId");
        string? limitName = GetString(bucket, "limitName");
        string? reachedType = GetString(bucket, "rateLimitReachedType");
        var windows = new List<UsageWindowSnapshot>();
        AddWindow(bucket, "primary", limitId, limitName, reachedType, receivedAt, windows);
        AddWindow(bucket, "secondary", limitId, limitName, reachedType, receivedAt, windows);
        UsageAvailability windowAvailability = windows.Count == 0 ? UsageAvailability.Unsupported : UsageAvailability.Available;
        string? planType = GetString(bucket, "planType");
        CodexCreditSnapshot? credits = ParseCredits(bucket);
        CodexIndividualLimitSnapshot? individualLimit = ParseIndividualLimit(bucket, receivedAt);
        bool hasComponent = windows.Count != 0 || credits is not null || individualLimit is not null;
        UsageAvailability availability = hasComponent
            ? UsageAvailability.Available
            : UsageAvailability.Unsupported;
        return new(
            UsageProvider.Codex,
            receivedAt,
            DateTimeOffset.UtcNow,
            availability,
            availability == UsageAvailability.Available ? null : "SCHEMA_UNSUPPORTED",
            planType,
            windows,
            credits?.Balance,
            false,
            availability == UsageAvailability.Available ? receivedAt : null,
            CreditSnapshot: credits,
            IndividualLimit: individualLimit,
            RateLimitAvailability: windowAvailability,
            RateLimitReason: windowAvailability == UsageAvailability.Available ? null : "SCHEMA_UNSUPPORTED");
    }

    private static CodexCreditSnapshot? ParseCredits(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("credits", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object ||
            !TryBoolean(value, "hasCredits", out bool hasCredits) ||
            !TryBoolean(value, "unlimited", out bool unlimited))
            return null;
        if (unlimited)
            return new(hasCredits, true, null);

        decimal? balance = null;
        if (value.TryGetProperty("balance", out JsonElement balanceValue) &&
            balanceValue.ValueKind != JsonValueKind.Null)
        {
            if (balanceValue.ValueKind != JsonValueKind.String ||
                !TryDecimal(balanceValue.GetString(), out decimal parsed))
                return null;
            balance = parsed;
        }
        return new(hasCredits, unlimited, balance);
    }

    private static CodexIndividualLimitSnapshot? ParseIndividualLimit(
        JsonElement bucket,
        DateTimeOffset receivedAt)
    {
        if (!bucket.TryGetProperty("individualLimit", out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object ||
            !TryDecimal(GetString(value, "used"), out decimal used) ||
            !TryDecimal(GetString(value, "limit"), out decimal limit) ||
            limit <= 0 ||
            !value.TryGetProperty("remainingPercent", out JsonElement remainingValue) ||
            !remainingValue.TryGetInt32(out int remainingPercent))
            return null;
        DateTimeOffset? reset = null;
        if (value.TryGetProperty("resetsAt", out JsonElement resetValue) &&
            resetValue.ValueKind == JsonValueKind.Number &&
            resetValue.TryGetInt64(out long seconds))
        {
            try
            {
                DateTimeOffset candidate = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (candidate >= receivedAt.AddYears(-1) && candidate <= receivedAt.AddYears(1))
                    reset = candidate;
            }
            catch (ArgumentOutOfRangeException) { }
        }
        int normalizedRemaining = used > limit ? 0 : Math.Clamp(remainingPercent, 0, 100);
        return new(used, limit, normalizedRemaining, reset);
    }

    private static bool TryDecimal(string? text, out decimal value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text) || text.AsSpan().Trim().Length != text.Length)
            return false;
        return decimal.TryParse(
            text,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value) && value >= 0;
    }

    private static bool TryBoolean(JsonElement element, string property, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(property, out JsonElement candidate) ||
            candidate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;
        value = candidate.GetBoolean();
        return true;
    }

    private static void AddWindow(JsonElement bucket, string property, string? limitId, string? limitName, string? reachedType, DateTimeOffset receivedAt, List<UsageWindowSnapshot> output)
    {
        if (!bucket.TryGetProperty(property, out JsonElement window) || window.ValueKind != JsonValueKind.Object || !TryNumber(window, "usedPercent", out double used) || !double.IsFinite(used)) return;
        int? duration = window.TryGetProperty("windowDurationMins", out JsonElement durationElement) && durationElement.TryGetInt32(out int value) ? value : null;
        DateTimeOffset? reset = null;
        if (window.TryGetProperty("resetsAt", out JsonElement resetElement) && resetElement.TryGetInt64(out long seconds))
        {
            try
            {
                DateTimeOffset candidate = DateTimeOffset.FromUnixTimeSeconds(seconds);
                if (candidate >= receivedAt.AddYears(-1) && candidate <= receivedAt.AddYears(1)) reset = candidate;
            }
            catch (ArgumentOutOfRangeException) { }
        }
        output.Add(new(limitId, limitName, property, used, duration, reset, reachedType));
    }

    private static string? GetString(JsonElement element, string property) => element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryNumber(JsonElement element, string property, out double value) { value = 0; return element.TryGetProperty(property, out JsonElement number) && number.TryGetDouble(out value); }
    private async Task ResetAsync() { if (_server is not null) { await _server.DisposeAsync().ConfigureAwait(false); _server = null; } }
    private void OnNotification(string method) { if (method == "account/rateLimits/updated") RateLimitsUpdated?.Invoke(); }
    public async ValueTask DisposeAsync() => await ResetAsync().ConfigureAwait(false);

    private static UsageSnapshot Unavailable(
        DateTimeOffset now,
        UsageAvailability availability,
        string reason) =>
        new(UsageProvider.Codex, now, DateTimeOffset.UtcNow, availability, reason, null, [], null, false, null);

    private static CodexAccountReadResult Result(
        UsageSnapshot snapshot,
        AccountIdentity? identity = null) =>
        new(snapshot, identity ?? new(null));
}
