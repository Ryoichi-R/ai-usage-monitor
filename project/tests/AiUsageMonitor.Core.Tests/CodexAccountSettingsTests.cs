using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.Core.Tests;

public sealed class CodexAccountSettingsTests
{
    [Fact]
    public void VersionOneSettingsMigrateWithMonetaryDisplayOff()
    {
        AppSettings result = new AppSettings
        {
            SchemaVersion = 1,
            ShowCredits = true,
            CodexMonetarySettingsInitialized = false,
            CodexAccounts = [],
        }.Normalized();
        Assert.Equal(2, result.SchemaVersion);
        Assert.False(result.ShowCredits);
        Assert.False(result.ShowAdditionalUsage);
        Assert.Single(result.CodexAccounts);
        Assert.Equal(CodexAccountSettings.DefaultAccountId, result.CodexAccounts[0].Id);
    }

    [Fact]
    public void NormalizationDropsInvalidAdditionalAccountsWithoutThrowing()
    {
        AppSettings result = new AppSettings
        {
            CodexAccounts =
            [
                new() { Id = "default", DisplayName = " CODEX " },
                new() { Id = "bad", DisplayName = "Bad", CodexHomePath = "relative" },
                new() { Id = "default", DisplayName = "Duplicate" },
            ],
        }.Normalized();
        CodexAccountSettings account = Assert.Single(result.CodexAccounts);
        Assert.Equal("CODEX", account.DisplayName);
    }

    [Fact]
    public void ValidatorRejectsDuplicateEffectiveHomes()
    {
        string profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "profile"));
        string home = Path.Combine(profile, ".codex");
        var settings = new AppSettings
        {
            CodexAccounts =
            [
                CodexAccountSettings.Default,
                new() { Id = "other", DisplayName = "Other", CodexHomePath = home },
            ],
        };
        IReadOnlyList<SettingsValidationError> errors =
            AppSettingsValidator.ValidateForSave(settings, new(null, profile));
        Assert.Contains(errors, error => error.Field == nameof(CodexAccountSettings.CodexHomePath));
    }

    [Fact]
    public void ResolverUsesConfiguredInheritedAndDefaultHomesInOrder()
    {
        string profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "profile"));
        string inherited = Path.Combine(profile, "inherited");
        string configured = Path.Combine(profile, "configured") + Path.DirectorySeparatorChar;
        var inheritedResolver = new CodexHomePathResolver(inherited, profile);
        Assert.Equal(
            configured.TrimEnd(Path.DirectorySeparatorChar),
            inheritedResolver.TryResolveEffectivePath(configured));
        Assert.Equal(inherited, inheritedResolver.TryResolveEffectivePath(null));
        Assert.Equal(
            Path.Combine(profile, ".codex"),
            new CodexHomePathResolver(null, profile).TryResolveEffectivePath(null));
        Assert.Null(CodexHomePathResolver.TryNormalizeAbsolute("relative"));
        Assert.Null(CodexHomePathResolver.TryNormalizeAbsolute(null));
    }

    [Fact]
    public void ValidatorReportsIdsNamesAndAdditionalHome()
    {
        string profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "profile"));
        var settings = new AppSettings
        {
            CodexAccounts =
            [
                new() { Id = "same", DisplayName = "First", CodexHomePath = Path.Combine(profile, "first") },
                new() { Id = "same", DisplayName = " ", CodexHomePath = null },
            ],
        };
        IReadOnlyList<SettingsValidationError> errors =
            AppSettingsValidator.ValidateForSave(settings, new(null, profile));
        Assert.Contains(errors, error => error.Field == nameof(CodexAccountSettings.Id));
        Assert.Contains(errors, error => error.Field == nameof(CodexAccountSettings.DisplayName));
        Assert.Contains(errors, error => error.Message.Contains("絶対パス", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsLongNameMissingDirectoryAndDefaultHome()
    {
        string profile = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "profile"));
        string missing = Path.Combine(profile, "missing");
        var settings = new AppSettings
        {
            CodexAccounts =
            [
                CodexAccountSettings.Default with { CodexHomePath = profile },
                new()
                {
                    Id = "other",
                    DisplayName = new string('x', 33),
                    CodexHomePath = missing,
                },
            ],
        };

        IReadOnlyList<SettingsValidationError> errors =
            AppSettingsValidator.ValidateForSave(settings, new(null, profile), _ => false);

        Assert.Contains(errors, error =>
            error.AccountId == CodexAccountSettings.DefaultAccountId &&
            error.Field == nameof(CodexAccountSettings.CodexHomePath));
        Assert.Contains(errors, error =>
            error.AccountId == "other" &&
            error.Field == nameof(CodexAccountSettings.DisplayName));
        Assert.Contains(errors, error =>
            error.AccountId == "other" &&
            error.Message.Contains("存在しません", StringComparison.Ordinal));
    }

    [Fact]
    public void NormalizationHandlesNullElementsAndKeepsMissingAbsoluteHome()
    {
        string missing = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "missing-" + Guid.NewGuid().ToString("N")));
        AppSettings result = new AppSettings
        {
            CodexAccounts =
            [
                null!,
                new()
                {
                    Id = "other",
                    DisplayName = new string('a', 33),
                    CodexHomePath = missing,
                },
            ],
        }.Normalized();

        CodexAccountSettings account = Assert.Single(result.CodexAccounts);
        Assert.Equal("other", account.Id);
        Assert.Equal(32, account.DisplayName.Length);
        Assert.Equal(missing, account.CodexHomePath);
    }
}
