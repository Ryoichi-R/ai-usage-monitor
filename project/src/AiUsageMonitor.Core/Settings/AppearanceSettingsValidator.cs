using System.Text;

namespace AiUsageMonitor.Core.Settings;

public sealed record AppearanceValidationError(string Field, string Message);

/// <summary>Pure validation and normalization for widget appearance settings.</summary>
public static class AppearanceSettingsValidator
{
    public const string DefaultMutedColor = "#FFB0B0B0";
    public const string DefaultAccentColor = "#FF7DC8FF";
    public const string DefaultWarningColor = "#FFFFB74D";
    public const string DefaultDangerColor = "#FFFF6B6B";
    public const string DefaultBackgroundColor = "#FF000000";
    public const double DefaultBackgroundOpacity = 0.69;
    public const double DefaultBackgroundEdgeFadePercent = 22;

    public static string? NormalizeFontFamilyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string trimmed = value.Trim();
        return trimmed.Length <= 128 && !trimmed.Any(char.IsControl) ? trimmed : null;
    }

    public static string? NormalizeOptionalColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TryNormalizeColor(value, out string normalized) ? normalized : null;
    }

    public static string NormalizeColor(string? value, string fallback)
    {
        return TryNormalizeColor(value, out string normalized)
            ? normalized
            : fallback;
    }

    public static string NormalizeOpaqueColor(string? value)
    {
        if (!TryNormalizeColor(value, out string normalized)) return DefaultBackgroundColor;
        return "#FF" + normalized[3..];
    }

    public static bool TryNormalizeColor(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string candidate = value.Trim();
        if (candidate.Length is not (7 or 9) || candidate[0] != '#') return false;
        for (int index = 1; index < candidate.Length; index++)
        {
            if (!Uri.IsHexDigit(candidate[index])) return false;
        }
        normalized = candidate.Length == 7
            ? "#FF" + candidate[1..].ToUpperInvariant()
            : "#" + candidate[1..].ToUpperInvariant();
        return true;
    }

    public static IReadOnlyList<AppearanceValidationError> Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<AppearanceValidationError>();
        if (!IsValidFont(settings.FontFamilyName))
            errors.Add(new(nameof(AppSettings.FontFamilyName), "フォント名は128文字以内で、制御文字を含めないでください。"));
        ValidateColor(errors, nameof(AppSettings.ForegroundColor), settings.ForegroundColor, optional: true);
        ValidateColor(errors, nameof(AppSettings.MutedColor), settings.MutedColor);
        ValidateColor(errors, nameof(AppSettings.AccentColor), settings.AccentColor);
        ValidateColor(errors, nameof(AppSettings.WarningColor), settings.WarningColor);
        ValidateColor(errors, nameof(AppSettings.DangerColor), settings.DangerColor);
        ValidateColor(errors, nameof(AppSettings.BackgroundColor), settings.BackgroundColor);
        if (!double.IsFinite(settings.BackgroundOpacity) || settings.BackgroundOpacity is < 0 or > 1)
            errors.Add(new(nameof(AppSettings.BackgroundOpacity), "背景の不透明度は0～100%で入力してください。"));
        if (!Enum.IsDefined(settings.BackgroundFillMode))
            errors.Add(new(nameof(AppSettings.BackgroundFillMode), "背景の塗り方が不正です。"));
        if (!double.IsFinite(settings.BackgroundEdgeFadePercent) || settings.BackgroundEdgeFadePercent is < 5 or > 50)
            errors.Add(new(nameof(AppSettings.BackgroundEdgeFadePercent), "フェード幅は5～50%で入力してください。"));
        return errors;
    }

    private static void ValidateColor(
        List<AppearanceValidationError> errors,
        string field,
        string? value,
        bool optional = false)
    {
        if (optional && string.IsNullOrWhiteSpace(value)) return;
        if (!TryNormalizeColor(value, out _))
            errors.Add(new(field, "色は #RRGGBB または #AARRGGBB で入力してください。"));
    }

    private static bool IsValidFont(string? value) =>
        string.IsNullOrWhiteSpace(value) ||
        (value.Trim().Length <= 128 && !value.Any(char.IsControl));
}
