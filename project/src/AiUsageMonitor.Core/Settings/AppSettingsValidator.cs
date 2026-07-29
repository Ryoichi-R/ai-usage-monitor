namespace AiUsageMonitor.Core.Settings;

public sealed record SettingsValidationError(string AccountId, string Field, string Message);

public static class AppSettingsValidator
{
    public static IReadOnlyList<SettingsValidationError> ValidateForSave(
        AppSettings settings,
        CodexHomePathResolver resolver,
        Func<string, bool>? directoryExists = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(resolver);
        directoryExists ??= Directory.Exists;

        var errors = new List<SettingsValidationError>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var homes = new HashSet<string>(CodexHomePathResolver.PathComparer);
        foreach (CodexAccountSettings? account in settings.CodexAccounts ?? [])
        {
            if (account is null)
            {
                errors.Add(new(string.Empty, nameof(AppSettings.CodexAccounts), "空のアカウント設定を保存できません。"));
                continue;
            }

            string accountId = account.Id?.Trim() ?? string.Empty;
            if (accountId.Length == 0 || !ids.Add(accountId))
                errors.Add(new(accountId, nameof(account.Id), "アカウントIDは空にせず、一意にしてください。"));

            string displayName = account.DisplayName?.Trim() ?? string.Empty;
            if (displayName.Length is < 1 or > 32)
                errors.Add(new(accountId, nameof(account.DisplayName), "表示名は1～32文字で入力してください。"));

            bool isDefault = string.Equals(
                accountId,
                CodexAccountSettings.DefaultAccountId,
                StringComparison.OrdinalIgnoreCase);
            string? normalizedHome = CodexHomePathResolver.TryNormalizeAbsolute(account.CodexHomePath);
            if (isDefault && !string.IsNullOrWhiteSpace(account.CodexHomePath))
            {
                errors.Add(new(accountId, nameof(account.CodexHomePath), "既定アカウントのCODEX_HOMEは空欄にしてください。"));
            }
            else if (!isDefault)
            {
                if (normalizedHome is null)
                {
                    errors.Add(new(accountId, nameof(account.CodexHomePath), "追加アカウントには絶対パスのCODEX_HOMEが必要です。"));
                }
                else if (!DirectoryExistsSafely(directoryExists, normalizedHome))
                {
                    errors.Add(new(accountId, nameof(account.CodexHomePath), "指定したCODEX_HOMEフォルダーが存在しません。"));
                }
            }

            string? effective = resolver.TryResolveEffectivePath(account.CodexHomePath);
            if (effective is not null && !homes.Add(effective))
                errors.Add(new(accountId, nameof(account.CodexHomePath), "CODEX_HOMEが別のアカウントと重複しています。"));
        }
        return errors;
    }

    private static bool DirectoryExistsSafely(Func<string, bool> directoryExists, string path)
    {
        try
        {
            return directoryExists(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
