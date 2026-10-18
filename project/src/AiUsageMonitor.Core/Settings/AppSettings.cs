using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiUsageMonitor.Core.Settings;

public enum PlacementAnchor { TopRight, BottomRight, TopLeft, BottomLeft }
public enum PlacementMode { Preset, Custom }
public enum WidgetDisplayMode { Standard, Compact }
public enum BackgroundFillMode { Solid, EdgeFade }

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 2;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public int RefreshIntervalSeconds { get; init; } = 300;
    public int StartupTimeoutSeconds { get; init; } = 30;
    public string? CodexExecutablePath { get; init; }
    public bool ShowCredits { get; init; }
    public bool ShowAdditionalUsage { get; init; }
    public bool CodexMonetarySettingsInitialized { get; init; }
    public bool ShowCodexUsage { get; init; } = true;
    public IReadOnlyList<CodexAccountSettings> CodexAccounts { get; init; } = [CodexAccountSettings.Default];
    public bool ShowClaudeUsage { get; init; }
    public ClaudeUsageAcquisitionMode ClaudeUsageAcquisitionMode { get; init; } = ClaudeUsageAcquisitionMode.Automatic;
    public string? ClaudeExecutablePath { get; init; }
    public bool ClaudeSetupCompleted { get; init; }
    public bool ClickThrough { get; init; } = true;
    public bool AlwaysOnTop { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public double UiScalePercent { get; init; } = 100;
    public WidgetDisplayMode DisplayMode { get; init; } = WidgetDisplayMode.Standard;
    public double Opacity { get; init; } = 1;
    public string? FontFamilyName { get; init; }
    public string? ForegroundColor { get; init; }
    public string MutedColor { get; init; } = AppearanceSettingsValidator.DefaultMutedColor;
    public string AccentColor { get; init; } = AppearanceSettingsValidator.DefaultAccentColor;
    public string WarningColor { get; init; } = AppearanceSettingsValidator.DefaultWarningColor;
    public string DangerColor { get; init; } = AppearanceSettingsValidator.DefaultDangerColor;
    public bool BackgroundEnabled { get; init; }
    public string BackgroundColor { get; init; } = AppearanceSettingsValidator.DefaultBackgroundColor;
    public double BackgroundOpacity { get; init; } = AppearanceSettingsValidator.DefaultBackgroundOpacity;
    public BackgroundFillMode BackgroundFillMode { get; init; } = BackgroundFillMode.Solid;
    public double BackgroundEdgeFadePercent { get; init; } = AppearanceSettingsValidator.DefaultBackgroundEdgeFadePercent;
    public bool HideBackgroundBehindWindows { get; init; }
    public string? MonitorDeviceName { get; init; }
    /// <summary>
    /// モニタのdevice interface path。<see cref="MonitorDeviceName"/>（DISPLAYn）は接続変更で振り直されるため、
    /// 値がある場合はこちらだけで表示先を照合する。旧設定との互換のためnullを許容する。
    /// </summary>
    public string? MonitorStableId { get; init; }
    public PlacementAnchor Anchor { get; init; } = PlacementAnchor.TopRight;
    public PlacementMode PlacementMode { get; init; } = PlacementMode.Preset;
    public double HorizontalMarginDip { get; init; } = 12;
    public double VerticalMarginDip { get; init; } = 12;
    public double? CustomLeftFraction { get; init; }
    public double? CustomTopFraction { get; init; }
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; init; }

    public AppSettings Normalized()
    {
        bool monetaryInitialized = CodexMonetarySettingsInitialized;
        List<CodexAccountSettings> accounts = NormalizeAccounts(CodexAccounts);
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 60, 900),
            StartupTimeoutSeconds = Math.Clamp(StartupTimeoutSeconds, 5, 120),
            UiScalePercent = double.IsFinite(UiScalePercent) ? Math.Clamp(UiScalePercent, 75, 200) : 100,
            Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .2, 1) : 1,
            FontFamilyName = AppearanceSettingsValidator.NormalizeFontFamilyName(FontFamilyName),
            ForegroundColor = AppearanceSettingsValidator.NormalizeOptionalColor(ForegroundColor),
            MutedColor = AppearanceSettingsValidator.NormalizeColor(MutedColor, AppearanceSettingsValidator.DefaultMutedColor),
            AccentColor = AppearanceSettingsValidator.NormalizeColor(AccentColor, AppearanceSettingsValidator.DefaultAccentColor),
            WarningColor = AppearanceSettingsValidator.NormalizeColor(WarningColor, AppearanceSettingsValidator.DefaultWarningColor),
            DangerColor = AppearanceSettingsValidator.NormalizeColor(DangerColor, AppearanceSettingsValidator.DefaultDangerColor),
            BackgroundColor = AppearanceSettingsValidator.NormalizeOpaqueColor(BackgroundColor),
            BackgroundOpacity = double.IsFinite(BackgroundOpacity) ? Math.Clamp(BackgroundOpacity, 0, 1) : DefaultBackgroundOpacity,
            BackgroundFillMode = Enum.IsDefined(BackgroundFillMode) ? BackgroundFillMode : BackgroundFillMode.Solid,
            BackgroundEdgeFadePercent = double.IsFinite(BackgroundEdgeFadePercent)
                ? Math.Clamp(BackgroundEdgeFadePercent, 5, 50)
                : AppearanceSettingsValidator.DefaultBackgroundEdgeFadePercent,
            HorizontalMarginDip = double.IsFinite(HorizontalMarginDip) ? Math.Clamp(HorizontalMarginDip, 0, 200) : 12,
            VerticalMarginDip = double.IsFinite(VerticalMarginDip) ? Math.Clamp(VerticalMarginDip, 0, 200) : 12,
            CustomLeftFraction = NormalizeFraction(CustomLeftFraction),
            CustomTopFraction = NormalizeFraction(CustomTopFraction),
            MonitorStableId = string.IsNullOrWhiteSpace(MonitorStableId) ? null : MonitorStableId.Trim(),
            CodexExecutablePath = string.IsNullOrWhiteSpace(CodexExecutablePath) ? null : CodexExecutablePath.Trim(),
            ClaudeExecutablePath = string.IsNullOrWhiteSpace(ClaudeExecutablePath) ? null : ClaudeExecutablePath.Trim(),
            ClaudeUsageAcquisitionMode = Enum.IsDefined(ClaudeUsageAcquisitionMode)
            ? ClaudeUsageAcquisitionMode
            : ClaudeUsageAcquisitionMode.Automatic,
            DisplayMode = Enum.IsDefined(DisplayMode)
                ? DisplayMode
                : WidgetDisplayMode.Standard,
            ShowCredits = monetaryInitialized && ShowCredits,
            ShowAdditionalUsage = monetaryInitialized && ShowAdditionalUsage,
            CodexAccounts = accounts,
        };
    }

    public string EffectiveBackgroundRgb => AppearanceSettingsValidator.NormalizeOpaqueColor(BackgroundColor)[3..];

    public double EffectiveBackgroundOpacity => BackgroundEnabled
        ? Math.Clamp(BackgroundOpacity, 0, 1)
        : 0;

    private const double DefaultBackgroundOpacity = AppearanceSettingsValidator.DefaultBackgroundOpacity;

    private static List<CodexAccountSettings> NormalizeAccounts(
        IReadOnlyList<CodexAccountSettings>? accounts)
    {
        var normalized = new List<CodexAccountSettings>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CodexAccountSettings? candidate in accounts ?? [])
        {
            if (candidate is null) continue;
            string id = candidate.Id?.Trim() ?? string.Empty;
            string displayName = candidate.DisplayName?.Trim() ?? string.Empty;
            if (id.Length == 0 || displayName.Length == 0 || !ids.Add(id)) continue;
            string? home = string.IsNullOrWhiteSpace(candidate.CodexHomePath)
                ? null
                : CodexHomePathResolver.TryNormalizeAbsolute(candidate.CodexHomePath);
            if (!string.Equals(id, CodexAccountSettings.DefaultAccountId, StringComparison.OrdinalIgnoreCase) &&
                home is null) continue;
            normalized.Add(candidate with
            {
                Id = id,
                DisplayName = displayName[..Math.Min(displayName.Length, 32)],
                CodexHomePath = home,
            });
        }
        return normalized.Count == 0 ? [CodexAccountSettings.Default] : normalized;
    }

    private static double? NormalizeFraction(double? value) => value is { } number && double.IsFinite(number)
        ? Math.Clamp(number, 0, 1)
        : null;
}
