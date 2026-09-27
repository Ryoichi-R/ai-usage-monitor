using System.Collections.ObjectModel;
using System.Globalization;
using AiUsageMonitor.App.UI.Runtime;
using AiUsageMonitor.App.UI.Settings;
using AiUsageMonitor.Claude.Configuration;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PlacementMode = AiUsageMonitor.Core.Settings.PlacementMode;

namespace AiUsageMonitor.App.UI.Views;

/// <summary>
/// 設定画面。WPF版<c>SettingsWindow</c>の移植で、検証・保存・配置の取り込みは同じ規則を使う。
/// モニター一覧・statusLine設定例はhostが渡し、フォルダー選択・色選択・クリップボードはAvaloniaの仕組みを使う。
/// </summary>
public partial class SettingsWindow : Window
{
    internal const int GeneralTabIndex = 0;
    internal const int PlacementTabIndex = 1;
    internal const int AppearanceTabIndex = 2;
    internal const int CodexTabIndex = 3;

    private readonly Func<AppSettings> _captureCurrentPosition;
    private readonly Func<UsageSnapshot>? _claudeStatusProvider;
    private readonly Func<string?>? _claudeSourceProvider;
    private readonly Func<ClaudeRuntimeHealth>? _claudeHealthProvider;
    private readonly DispatcherTimer _claudeStatusTimer;
    private readonly ObservableCollection<CodexAccountEditor> _codexAccounts = [];
    private AppSettings _settings;
    private PlacementMode _placementMode;
    private bool _suppressPlacementModeChange;

    // XAMLローダー（デザイナー）用。
    public SettingsWindow()
        : this(new AppSettings().Normalized(), () => new AppSettings().Normalized())
    {
    }

    public SettingsWindow(
        AppSettings settings,
        Func<AppSettings> captureCurrentPosition,
        Func<UsageSnapshot>? claudeStatusProvider = null,
        Func<string?>? claudeSourceProvider = null,
        Func<ClaudeRuntimeHealth>? claudeHealthProvider = null,
        IReadOnlyList<MonitorChoice>? connectedMonitors = null,
        string? claudeSetupExample = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(captureCurrentPosition);
        InitializeComponent();
        _settings = settings;
        _placementMode = settings.PlacementMode;
        _captureCurrentPosition = captureCurrentPosition;
        _claudeStatusProvider = claudeStatusProvider;
        _claudeSourceProvider = claudeSourceProvider;
        _claudeHealthProvider = claudeHealthProvider;
        if (!OperatingSystem.IsWindows()) StartupBox.Content = "ログイン時に自動起動する";

        MonitorBox.Items.Add(MonitorChoice.Automatic);
        foreach (MonitorChoice monitor in connectedMonitors ?? [])
            MonitorBox.Items.Add(monitor);
        SelectMonitor(settings.MonitorDeviceName, settings.MonitorStableId);
        AnchorBox.ItemsSource = new[]
        {
            new AnchorChoice(PlacementAnchor.TopRight, "右上"),
            new AnchorChoice(PlacementAnchor.BottomRight, "右下"),
            new AnchorChoice(PlacementAnchor.TopLeft, "左上"),
            new AnchorChoice(PlacementAnchor.BottomLeft, "左下"),
        };
        SelectAnchor(settings.Anchor);
        ClickThroughBox.IsChecked = settings.ClickThrough;
        TopmostBox.IsChecked = settings.AlwaysOnTop;
        StartupBox.IsChecked = settings.StartWithWindows;
        DisplayModeChoice[] displayModes =
        [
            new(WidgetDisplayMode.Standard, "標準表示"),
            new(WidgetDisplayMode.Compact, "縮小表示（主要な使用率のみ）"),
        ];
        DisplayModeBox.ItemsSource = displayModes;
        WidgetDisplayMode displayMode = Enum.IsDefined(settings.DisplayMode) ? settings.DisplayMode : WidgetDisplayMode.Standard;
        DisplayModeBox.SelectedItem = displayModes.First(choice => choice.Value == displayMode);
        ShowCodexBox.IsChecked = settings.ShowCodexUsage;
        ShowAdditionalUsageBox.IsChecked = settings.ShowAdditionalUsage;
        ShowCreditsBox.IsChecked = settings.ShowCredits;
        foreach (CodexAccountSettings account in settings.CodexAccounts)
            _codexAccounts.Add(new(account));
        CodexAccountsList.ItemsSource = _codexAccounts;
        ShowClaudeBox.IsChecked = settings.ShowClaudeUsage;
        ClaudeModeChoice[] claudeModes =
        [
            new(ClaudeUsageAcquisitionMode.Automatic, "自動（公式CLI優先、statusLineは参考）"),
            new(ClaudeUsageAcquisitionMode.StatusLineOnly, "statusLineのみ（受信時刻・最新性未保証）"),
            new(ClaudeUsageAcquisitionMode.OfficialCliOnly, "公式CLIの自動取得のみ"),
        ];
        ClaudeModeBox.ItemsSource = claudeModes;
        ClaudeModeBox.SelectedItem = claudeModes.First(choice => choice.Value == settings.ClaudeUsageAcquisitionMode);
        ClaudeExecutableBox.Text = settings.ClaudeExecutablePath ?? string.Empty;
        ClaudeExampleBox.Text = claudeSetupExample ?? ClaudeSetupExample.Create(AppContext.BaseDirectory);
        FontFamilyBox.Text = settings.FontFamilyName ?? string.Empty;
        ForegroundColorBox.Text = settings.ForegroundColor ?? string.Empty;
        MutedColorBox.Text = settings.MutedColor;
        AccentColorBox.Text = settings.AccentColor;
        WarningColorBox.Text = settings.WarningColor;
        DangerColorBox.Text = settings.DangerColor;
        BackgroundEnabledBox.IsChecked = settings.BackgroundEnabled;
        HideBackgroundBox.IsChecked = settings.HideBackgroundBehindWindows;
        BackgroundColorBox.Text = settings.BackgroundColor;
        BackgroundOpacityBox.Text = (settings.BackgroundOpacity * 100d).ToString("0", CultureInfo.CurrentCulture);
        BackgroundFillModeChoice[] fillModes =
        [
            new(BackgroundFillMode.Solid, "単色"),
            new(BackgroundFillMode.EdgeFade, "四方の端を透明化"),
        ];
        BackgroundFillModeBox.ItemsSource = fillModes;
        BackgroundFillModeBox.SelectedItem = fillModes.First(choice => choice.Value == settings.BackgroundFillMode);
        BackgroundFadeBox.Text = settings.BackgroundEdgeFadePercent.ToString("0", CultureInfo.CurrentCulture);
        HorizontalMarginBox.Text = settings.HorizontalMarginDip.ToString(CultureInfo.CurrentCulture);
        VerticalMarginBox.Text = settings.VerticalMarginDip.ToString(CultureInfo.CurrentCulture);
        ScaleBox.Text = settings.UiScalePercent.ToString(CultureInfo.CurrentCulture);
        ExecutableBox.Text = settings.CodexExecutablePath ?? string.Empty;
        RefreshBox.Text = settings.RefreshIntervalSeconds.ToString(CultureInfo.CurrentCulture);
        StartupTimeoutBox.Text = settings.StartupTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        PresetPlacementBox.IsChecked = settings.PlacementMode == PlacementMode.Preset;
        CustomPlacementBox.IsChecked = settings.PlacementMode == PlacementMode.Custom;

        BackgroundEnabledBox.IsCheckedChanged += OnAppearanceControlChanged;
        HideBackgroundBox.IsCheckedChanged += OnAppearanceControlChanged;
        TopmostBox.IsCheckedChanged += OnAppearanceControlChanged;
        BackgroundFillModeBox.SelectionChanged += OnAppearanceControlChanged;
        foreach (TextBox box in new[]
        {
            FontFamilyBox, ForegroundColorBox, MutedColorBox, AccentColorBox,
            WarningColorBox, DangerColorBox, BackgroundColorBox, BackgroundOpacityBox, BackgroundFadeBox,
        })
        {
            OnTextEdited(box, () => OnAppearanceControlChanged(box, EventArgs.Empty));
        }
        UpdateAppearanceControlState();
        PresetPlacementBox.IsCheckedChanged += (_, _) => { if (PresetPlacementBox.IsChecked == true) SelectPresetPlacement(); };
        CustomPlacementBox.IsCheckedChanged += (_, _) => { if (CustomPlacementBox.IsChecked == true) SelectCustomPlacement(); };
        MonitorBox.SelectionChanged += (_, _) => SelectPresetPlacement();
        AnchorBox.SelectionChanged += (_, _) => SelectPresetPlacement();
        OnTextEdited(HorizontalMarginBox, SelectPresetPlacement);
        OnTextEdited(VerticalMarginBox, SelectPresetPlacement);
        foreach (TextBox box in new[] { ScaleBox, HorizontalMarginBox, VerticalMarginBox, RefreshBox, StartupTimeoutBox })
            OnTextEdited(box, ClearValidation);
        SetPlacementMode(settings.PlacementMode);
        RefreshClaudeStatus();
        _claudeStatusTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshClaudeStatus());
        _claudeStatusTimer.Start();
        Closed += (_, _) => _claudeStatusTimer.Stop();
    }

    /// <summary>保存された設定。キャンセルまたは未保存で閉じた場合は開いたときの設定のまま。</summary>
    public AppSettings Result => _settings;

    /// <summary>保存ボタンで閉じた場合にtrue。</summary>
    public bool Saved { get; private set; }

    public event Action? ClaudeSetupRequested;

    internal IReadOnlyList<CodexAccountEditor> CodexAccountEditors => _codexAccounts;

    /// <summary>直近の検証失敗でフォーカスを移したアカウント欄（テスト用）。</summary>
    internal string? LastAccountFocusField { get; private set; }

    internal void Save()
    {
        if (!TryReadControls(_settings, out AppSettings controls)) return;
        var resolver = new CodexHomePathResolver(
            Environment.GetEnvironmentVariable("CODEX_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        IReadOnlyList<SettingsValidationError> errors = AppSettingsValidator.ValidateForSave(controls, resolver);
        if (errors.Count != 0)
        {
            CategoryTabs.SelectedIndex = CodexTabIndex;
            ValidationMessage.Text = errors[0].Message;
            FocusCodexValidation(errors[0]);
            return;
        }
        _settings = (_placementMode == PlacementMode.Custom
            ? controls with { PlacementMode = PlacementMode.Custom }
            : PlacementSettingsComposer.UsePreset(controls)).Normalized();
        ValidationMessage.Text = string.Empty;
        Saved = true;
        Close(true);
    }

    internal void SaveCurrentPosition()
    {
        AppSettings captured = _captureCurrentPosition();
        _settings = PlacementSettingsComposer.UseCapturedPosition(ReadControlsLenient(_settings), captured);
        SetPlacementMode(PlacementMode.Custom);
        _suppressPlacementModeChange = true;
        SelectMonitor(captured.MonitorDeviceName, captured.MonitorStableId);
        _suppressPlacementModeChange = false;
    }

    internal void ResetPosition()
    {
        SetPlacementMode(PlacementMode.Preset);
        _settings = PlacementSettingsComposer.UsePreset(ReadControlsLenient(_settings) with
        {
            MonitorDeviceName = null,
            MonitorStableId = null,
            Anchor = PlacementAnchor.TopRight,
            HorizontalMarginDip = 12,
            VerticalMarginDip = 12,
        });
        MonitorBox.SelectedIndex = 0;
        SelectAnchor(PlacementAnchor.TopRight);
        HorizontalMarginBox.Text = "12";
        VerticalMarginBox.Text = "12";
    }

    internal void AddCodexAccount()
    {
        var account = new CodexAccountEditor(new()
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = $"CODEX {_codexAccounts.Count + 1}",
            Enabled = true,
            ShowInWidget = true,
        });
        _codexAccounts.Add(account);
        CodexAccountsList.SelectedItem = account;
        CodexAccountsList.ScrollIntoView(account);
    }

    internal void RemoveCodexAccount()
    {
        if (CodexAccountsList.SelectedItem is not CodexAccountEditor selected) return;
        if (selected.IsDefault)
        {
            ValidationMessage.Text = "既定アカウントは削除できません。";
            return;
        }
        _codexAccounts.Remove(selected);
    }

    /// <summary>フォルダー選択の前提を確認する。選択できる場合は対象アカウントを返す。</summary>
    internal CodexAccountEditor? AccountForHomeSelection()
    {
        if (CodexAccountsList.SelectedItem is not CodexAccountEditor selected)
        {
            ValidationMessage.Text = "追加アカウントを選択してください。";
            return null;
        }
        if (selected.IsDefault)
        {
            ValidationMessage.Text = "既定アカウントは現在のCodex環境を継承します。";
            return null;
        }
        return selected;
    }

    internal async Task SelectCodexHomeAsync()
    {
        CodexAccountEditor? selected = AccountForHomeSelection();
        if (selected is null) return;
        IStorageProvider? storage = GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanPickFolder) return;
        IStorageFolder? start = Directory.Exists(selected.CodexHomePath)
            ? await storage.TryGetFolderFromPathAsync(selected.CodexHomePath!)
            : null;
        IReadOnlyList<IStorageFolder> folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "公式Codex CLIでサインイン済みのCODEX_HOMEを選択してください。",
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        string? path = folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
        if (path is not null)
        {
            selected.CodexHomePath = path;
            ValidationMessage.Text = string.Empty;
        }
    }

    internal void RequestClaudeSetup()
    {
        ShowClaudeBox.IsChecked = true;
        ClaudeSetupRequested?.Invoke();
    }

    internal async Task CopyClaudeExampleAsync()
    {
        IClipboard? clipboard = GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(ClaudeExampleBox.Text ?? string.Empty);
        ValidationMessage.Foreground = Brushes.DodgerBlue;
        ValidationMessage.Text = "Claude Code設定をクリップボードへコピーしました。";
    }

    internal void RefreshClaudeStatus()
    {
        string text = _claudeStatusProvider is null
            ? "接続状態は、アプリを起動しているときに確認できます。"
            : UsageStatusFormatter.FormatClaudeConnection(_claudeStatusProvider());
        string? source = _claudeSourceProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(source))
            text += Environment.NewLine + "現在表示元: " + source;
        if (_claudeHealthProvider is { } healthProvider)
        {
            ClaudeRuntimeHealth health = healthProvider();
            text += Environment.NewLine + "active最終試行: " + FormatChannelTime(health.ActiveLastAttemptAt);
            if (!string.IsNullOrWhiteSpace(health.ActiveFailureReason))
                text += $"（失敗: {health.ActiveFailureReason}）";
            text += Environment.NewLine + "statusLine最終受信: " + FormatChannelTime(health.PassiveLastReceivedAt);
        }
        ClaudeConnectionStatusText.Text = text;
    }

    /// <summary>設定値の色を、色選択の初期値に使う不透明色へ変換する。形式が不正ならnull。</summary>
    internal static Color? TryGetOpaqueColor(string? value)
    {
        if (!AppearanceSettingsValidator.TryNormalizeColor(value, out string normalized)) return null;
        string hex = normalized[3..];
        return Color.FromRgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    internal static string FormatOpaqueColor(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private bool TryReadControls(AppSettings basis, out AppSettings controls)
    {
        controls = basis;
        if (!TryReadDouble(ScaleBox, "表示倍率は75～200の数値で入力してください。", 75, 200, GeneralTabIndex, out double scale) ||
            !TryReadDouble(HorizontalMarginBox, "水平余白は0～200の数値で入力してください。", 0, 200, PlacementTabIndex, out double horizontalMargin) ||
            !TryReadDouble(VerticalMarginBox, "垂直余白は0～200の数値で入力してください。", 0, 200, PlacementTabIndex, out double verticalMargin) ||
            !TryReadDouble(BackgroundOpacityBox, "背景の不透明度は0～100%の数値で入力してください。", 0, 100, AppearanceTabIndex, out double backgroundOpacityPercent) ||
            !TryReadDouble(BackgroundFadeBox, "フェード幅は5～50%の数値で入力してください。", 5, 50, AppearanceTabIndex, out double backgroundFadePercent) ||
            !TryReadInt(RefreshBox, "更新間隔は60～900の整数で入力してください。", 60, 900, CodexTabIndex, out int refreshInterval) ||
            !TryReadInt(StartupTimeoutBox, "起動待ちは5～120の整数で入力してください。", 5, 120, CodexTabIndex, out int startupTimeout))
        {
            return false;
        }

        controls = ReadControlsLenient(basis) with
        {
            HorizontalMarginDip = horizontalMargin,
            VerticalMarginDip = verticalMargin,
            UiScalePercent = scale,
            RefreshIntervalSeconds = refreshInterval,
            StartupTimeoutSeconds = startupTimeout,
            BackgroundOpacity = backgroundOpacityPercent / 100d,
            BackgroundEdgeFadePercent = backgroundFadePercent,
        };
        IReadOnlyList<AppearanceValidationError> appearanceErrors = AppearanceSettingsValidator.Validate(controls);
        if (appearanceErrors.Count != 0)
        {
            FocusAppearanceValidation(appearanceErrors[0]);
            controls = basis;
            return false;
        }
        return true;
    }

    private AppSettings ReadControlsLenient(AppSettings basis) => basis with
    {
        ClickThrough = ClickThroughBox.IsChecked == true,
        AlwaysOnTop = TopmostBox.IsChecked == true,
        StartWithWindows = StartupBox.IsChecked == true,
        DisplayMode = DisplayModeBox.SelectedItem is DisplayModeChoice displayMode
            ? displayMode.Value
            : WidgetDisplayMode.Standard,
        MonitorDeviceName = (MonitorBox.SelectedItem as MonitorChoice)?.DeviceName,
        MonitorStableId = (MonitorBox.SelectedItem as MonitorChoice)?.StableId,
        Anchor = AnchorBox.SelectedItem is AnchorChoice anchor ? anchor.Value : PlacementAnchor.TopRight,
        HorizontalMarginDip = ParseDoubleOrFallback(HorizontalMarginBox.Text, basis.HorizontalMarginDip),
        VerticalMarginDip = ParseDoubleOrFallback(VerticalMarginBox.Text, basis.VerticalMarginDip),
        UiScalePercent = ParseDoubleOrFallback(ScaleBox.Text, basis.UiScalePercent),
        CodexExecutablePath = ExecutableBox.Text,
        RefreshIntervalSeconds = ParseIntOrFallback(RefreshBox.Text, basis.RefreshIntervalSeconds),
        StartupTimeoutSeconds = ParseIntOrFallback(StartupTimeoutBox.Text, basis.StartupTimeoutSeconds),
        ShowCodexUsage = ShowCodexBox.IsChecked == true,
        ShowAdditionalUsage = ShowAdditionalUsageBox.IsChecked == true,
        ShowCredits = ShowCreditsBox.IsChecked == true,
        CodexMonetarySettingsInitialized = true,
        CodexAccounts = _codexAccounts.Select(account => account.ToSettings()).ToArray(),
        ShowClaudeUsage = ShowClaudeBox.IsChecked == true,
        ClaudeUsageAcquisitionMode = ClaudeModeBox.SelectedItem is ClaudeModeChoice mode
            ? mode.Value
            : ClaudeUsageAcquisitionMode.Automatic,
        ClaudeExecutablePath = ClaudeExecutableBox.Text,
        FontFamilyName = FontFamilyBox.Text,
        ForegroundColor = ForegroundColorBox.Text,
        MutedColor = MutedColorBox.Text ?? string.Empty,
        AccentColor = AccentColorBox.Text ?? string.Empty,
        WarningColor = WarningColorBox.Text ?? string.Empty,
        DangerColor = DangerColorBox.Text ?? string.Empty,
        BackgroundEnabled = BackgroundEnabledBox.IsChecked == true,
        HideBackgroundBehindWindows = HideBackgroundBox.IsChecked == true,
        BackgroundColor = BackgroundColorBox.Text ?? string.Empty,
        BackgroundOpacity = ParseDoubleOrFallback(BackgroundOpacityBox.Text, basis.BackgroundOpacity * 100d) / 100d,
        BackgroundFillMode = BackgroundFillModeBox.SelectedItem is BackgroundFillModeChoice fillMode
            ? fillMode.Value
            : BackgroundFillMode.Solid,
        BackgroundEdgeFadePercent = ParseDoubleOrFallback(BackgroundFadeBox.Text, basis.BackgroundEdgeFadePercent),
    };

    private void FocusCodexValidation(SettingsValidationError error)
    {
        CodexAccountEditor? editor = _codexAccounts.FirstOrDefault(account =>
            string.Equals(account.Id, error.AccountId, StringComparison.OrdinalIgnoreCase));
        if (editor is null)
        {
            LastAccountFocusField = null;
            CodexAccountsList.Focus();
            return;
        }

        CodexAccountsList.SelectedItem = editor;
        CodexAccountsList.ScrollIntoView(editor);
        string boxName = error.Field == nameof(CodexAccountSettings.CodexHomePath)
            ? "AccountHomeBox"
            : "AccountDisplayNameBox";
        LastAccountFocusField = error.Field == nameof(CodexAccountSettings.CodexHomePath)
            ? nameof(CodexAccountSettings.CodexHomePath)
            : nameof(CodexAccountSettings.DisplayName);
        Dispatcher.UIThread.Post(() =>
        {
            TextBox? box = CodexAccountsList.ContainerFromItem(editor)?
                .GetVisualDescendants()
                .OfType<TextBox>()
                .FirstOrDefault(candidate => candidate.Name == boxName);
            box?.Focus();
            box?.SelectAll();
        });
    }

    private bool TryReadDouble(TextBox textBox, string message, double minimum, double maximum, int tabIndex, out double value)
    {
        if (double.TryParse(textBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) &&
            double.IsFinite(value) &&
            value >= minimum &&
            value <= maximum)
        {
            return true;
        }

        ShowValidation(message, tabIndex, textBox);
        return false;
    }

    private bool TryReadInt(TextBox textBox, string message, int minimum, int maximum, int tabIndex, out int value)
    {
        if (int.TryParse(textBox.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) &&
            value >= minimum &&
            value <= maximum)
        {
            return true;
        }

        ShowValidation(message, tabIndex, textBox);
        return false;
    }

    private void ShowValidation(string message, int tabIndex, TextBox textBox)
    {
        CategoryTabs.SelectedIndex = tabIndex;
        ValidationMessage.Foreground = Brushes.Crimson;
        ValidationMessage.Text = message;
        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }

    private void FocusAppearanceValidation(AppearanceValidationError error)
    {
        TextBox? textBox = error.Field switch
        {
            nameof(AppSettings.FontFamilyName) => FontFamilyBox,
            nameof(AppSettings.ForegroundColor) => ForegroundColorBox,
            nameof(AppSettings.MutedColor) => MutedColorBox,
            nameof(AppSettings.AccentColor) => AccentColorBox,
            nameof(AppSettings.WarningColor) => WarningColorBox,
            nameof(AppSettings.DangerColor) => DangerColorBox,
            nameof(AppSettings.BackgroundColor) => BackgroundColorBox,
            nameof(AppSettings.BackgroundOpacity) => BackgroundOpacityBox,
            nameof(AppSettings.BackgroundEdgeFadePercent) => BackgroundFadeBox,
            _ => null,
        };
        CategoryTabs.SelectedIndex = AppearanceTabIndex;
        ValidationMessage.Foreground = Brushes.Crimson;
        ValidationMessage.Text = error.Message;
        if (textBox is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            });
        }
    }

    // AvaloniaのTextBox.TextChangedはDispatcher経由で遅れて発生するため、コンストラクターで入れた初期値の
    // 変更が購読後に届き、配置モードの切替や検証表示の消去を誤って起こす。Textプロパティの変化を同期的に
    // 受け取り、購読前の初期値設定は対象外にする。
    private static void OnTextEdited(TextBox box, Action handler) =>
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) handler();
        };

    private void OnAppearanceControlChanged(object? sender, EventArgs e)
    {
        UpdateAppearanceControlState();
        ClearValidation();
    }

    internal void UpdateAppearanceControlState()
    {
        bool enabled = BackgroundEnabledBox.IsChecked == true;
        BackgroundColorBox.IsEnabled = enabled;
        BackgroundOpacityBox.IsEnabled = enabled;
        BackgroundFillModeBox.IsEnabled = enabled;
        bool edgeFade = enabled && BackgroundFillModeBox.SelectedItem is BackgroundFillModeChoice choice
            && choice.Value == BackgroundFillMode.EdgeFade;
        BackgroundFadeBox.IsEnabled = edgeFade;
        HideBackgroundBox.IsEnabled = enabled && TopmostBox.IsChecked == true;

        BackgroundPreview.Background = AppearanceSettingsValidator.TryNormalizeColor(BackgroundColorBox.Text, out string normalized)
            ? new SolidColorBrush(Color.Parse(normalized))
            : Brushes.Transparent;
        BackgroundPreview.Opacity = enabled
            ? Math.Clamp(ParseDoubleOrFallback(BackgroundOpacityBox.Text, 69) / 100d, 0, 1)
            : 0;
    }

    private void ClearValidation()
    {
        ValidationMessage.Foreground = Brushes.Crimson;
        ValidationMessage.Text = string.Empty;
    }

    private static string FormatChannelTime(DateTimeOffset? value) =>
        value is null
            ? "なし"
            : value.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void SelectPresetPlacement()
    {
        if (!_suppressPlacementModeChange) SetPlacementMode(PlacementMode.Preset);
    }

    private void SelectCustomPlacement()
    {
        if (!_suppressPlacementModeChange) SetPlacementMode(PlacementMode.Custom);
    }

    private void SetPlacementMode(PlacementMode mode)
    {
        _placementMode = mode;
        _suppressPlacementModeChange = true;
        PresetPlacementBox.IsChecked = mode == PlacementMode.Preset;
        CustomPlacementBox.IsChecked = mode == PlacementMode.Custom;
        _suppressPlacementModeChange = false;
        PlacementStatusText.Text = mode == PlacementMode.Preset
            ? "保存時に、選択したモニター・基準位置・余白を使用します。"
            : "保存済みの自由配置位置を使用します。";
    }

    private void SelectMonitor(string? deviceName, string? stableId)
    {
        MonitorChoice[] connected = MonitorBox.Items
            .OfType<MonitorChoice>()
            .Where(item => item != MonitorChoice.Automatic && !item.IsDisconnected)
            .ToArray();
        MonitorChoice choice = MonitorChoice.Select(connected, deviceName, stableId);
        if (!MonitorBox.Items.Contains(choice)) MonitorBox.Items.Add(choice);
        MonitorBox.SelectedItem = choice;
    }

    private void SelectAnchor(PlacementAnchor anchor) =>
        AnchorBox.SelectedItem = AnchorBox.Items.OfType<AnchorChoice>().First(item => item.Value == anchor);

    private static double ParseDoubleOrFallback(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double parsed) && double.IsFinite(parsed)
            ? parsed
            : fallback;

    private static int ParseIntOrFallback(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out int parsed) ? parsed : fallback;

    private void OnSaveClick(object? sender, RoutedEventArgs e) => Save();

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnSaveCurrentPositionClick(object? sender, RoutedEventArgs e) => SaveCurrentPosition();

    private void OnResetPositionClick(object? sender, RoutedEventArgs e) => ResetPosition();

    private void OnAddCodexAccountClick(object? sender, RoutedEventArgs e) => AddCodexAccount();

    private void OnRemoveCodexAccountClick(object? sender, RoutedEventArgs e) => RemoveCodexAccount();

    private async void OnSelectCodexHomeClick(object? sender, RoutedEventArgs e) => await SelectCodexHomeAsync();

    private void OnOpenClaudeSetupClick(object? sender, RoutedEventArgs e) => RequestClaudeSetup();

    private async void OnCopyClaudeExampleClick(object? sender, RoutedEventArgs e) => await CopyClaudeExampleAsync();

    private void OnSelectColorClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string targetName } button) return;
        TextBox? target = this.FindControl<TextBox>(targetName);
        if (target is null) return;
        var view = new ColorView
        {
            Color = TryGetOpaqueColor(target.Text) ?? Colors.Black,
            IsAlphaEnabled = false,
        };
        view.ColorChanged += (_, args) => target.Text = FormatOpaqueColor(args.NewColor);
        var flyout = new Flyout { Content = view, Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight };
        flyout.ShowAt(button);
    }
}
