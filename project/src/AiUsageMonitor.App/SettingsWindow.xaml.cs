using System.Globalization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using AiUsageMonitor.Claude.Configuration;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Usage;
using Controls = System.Windows.Controls;
using Forms = System.Windows.Forms;

namespace AiUsageMonitor.App;

public partial class SettingsWindow : Window
{
    private const int GeneralTabIndex = 0;
    private const int PlacementTabIndex = 1;
    private const int AppearanceTabIndex = 2;
    private const int CodexTabIndex = 3;

    private readonly Func<AppSettings> _captureCurrentPosition;
    private readonly Func<UsageSnapshot>? _claudeStatusProvider;
    private readonly Func<string?>? _claudeSourceProvider;
    private readonly Func<ClaudeRuntimeHealth>? _claudeHealthProvider;
    private readonly DispatcherTimer _claudeStatusTimer;
    private AppSettings _settings;
    private PlacementMode _placementMode;
    private bool _suppressPlacementModeChange;
    private readonly ObservableCollection<CodexAccountEditor> _codexAccounts = [];
    public AppSettings Result => _settings;
    public event Action? ClaudeSetupRequested;

    public SettingsWindow(
        AppSettings settings,
        Func<AppSettings> captureCurrentPosition,
        Func<UsageSnapshot>? claudeStatusProvider = null,
        Func<string?>? claudeSourceProvider = null,
        Func<ClaudeRuntimeHealth>? claudeHealthProvider = null)
    {
        InitializeComponent();
        _settings = settings;
        _placementMode = settings.PlacementMode;
        _captureCurrentPosition = captureCurrentPosition;
        _claudeStatusProvider = claudeStatusProvider;
        _claudeSourceProvider = claudeSourceProvider;
        _claudeHealthProvider = claudeHealthProvider;
        MonitorBox.Items.Add(new MonitorChoice(null, "自動（プライマリ）"));
        foreach (Forms.Screen screen in Forms.Screen.AllScreens) MonitorBox.Items.Add(new MonitorChoice(screen.DeviceName, screen.DeviceName + (screen.Primary ? "（プライマリ）" : string.Empty)));
        SelectMonitor(settings.MonitorDeviceName);
        AnchorBox.ItemsSource = new[]
        {
            new AnchorChoice(PlacementAnchor.TopRight, "右上"),
            new AnchorChoice(PlacementAnchor.BottomRight, "右下"),
            new AnchorChoice(PlacementAnchor.TopLeft, "左上"),
            new AnchorChoice(PlacementAnchor.BottomLeft, "左下"),
        };
        SelectAnchor(settings.Anchor);
        ClickThroughBox.IsChecked = settings.ClickThrough; TopmostBox.IsChecked = settings.AlwaysOnTop; StartupBox.IsChecked = settings.StartWithWindows;
        DisplayModeBox.ItemsSource = new[]
        {
            new DisplayModeChoice(WidgetDisplayMode.Standard, "標準表示"),
            new DisplayModeChoice(WidgetDisplayMode.Compact, "縮小表示（主要な使用率のみ）"),
        };
        DisplayModeBox.SelectedItem = DisplayModeBox.Items
            .Cast<DisplayModeChoice>()
            .First(choice => choice.Value == (Enum.IsDefined(settings.DisplayMode)
                ? settings.DisplayMode
                : WidgetDisplayMode.Standard));
        ShowCodexBox.IsChecked = settings.ShowCodexUsage;
        ShowAdditionalUsageBox.IsChecked = settings.ShowAdditionalUsage;
        ShowCreditsBox.IsChecked = settings.ShowCredits;
        foreach (CodexAccountSettings account in settings.CodexAccounts)
            _codexAccounts.Add(new(account));
        CodexAccountsGrid.ItemsSource = _codexAccounts;
        ShowClaudeBox.IsChecked = settings.ShowClaudeUsage;
        ClaudeModeBox.ItemsSource = new[]
        {
            new ClaudeModeChoice(ClaudeUsageAcquisitionMode.Automatic, "自動（公式CLI優先、statusLineは参考）"),
            new ClaudeModeChoice(ClaudeUsageAcquisitionMode.StatusLineOnly, "statusLineのみ（受信時刻・最新性未保証）"),
            new ClaudeModeChoice(ClaudeUsageAcquisitionMode.OfficialCliOnly, "公式CLIの自動取得のみ"),
        };
        ClaudeModeBox.SelectedItem = ClaudeModeBox.Items
            .Cast<ClaudeModeChoice>()
            .First(choice => choice.Value == settings.ClaudeUsageAcquisitionMode);
        ClaudeExecutableBox.Text = settings.ClaudeExecutablePath ?? string.Empty;
        ClaudeExampleBox.Text = ClaudeSetupExample.Create(AppContext.BaseDirectory);
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
        BackgroundFillModeBox.ItemsSource = new[]
        {
            new BackgroundFillModeChoice(BackgroundFillMode.Solid, "単色"),
            new BackgroundFillModeChoice(BackgroundFillMode.EdgeFade, "四方の端を透明化"),
        };
        BackgroundFillModeBox.SelectedItem = BackgroundFillModeBox.Items
            .Cast<BackgroundFillModeChoice>()
            .First(choice => choice.Value == settings.BackgroundFillMode);
        BackgroundFadeBox.Text = settings.BackgroundEdgeFadePercent.ToString("0", CultureInfo.CurrentCulture);
        BackgroundFillModeBox.SelectionChanged += AppearanceControlChanged;
        TopmostBox.Checked += AppearanceControlChanged;
        TopmostBox.Unchecked += AppearanceControlChanged;
        foreach (Controls.TextBox box in new[]
        {
            FontFamilyBox, ForegroundColorBox, MutedColorBox, AccentColorBox,
            WarningColorBox, DangerColorBox, BackgroundColorBox, BackgroundOpacityBox, BackgroundFadeBox,
        })
        {
            box.TextChanged += AppearanceControlChanged;
        }
        UpdateAppearanceControlState();
        HorizontalMarginBox.Text = settings.HorizontalMarginDip.ToString(CultureInfo.CurrentCulture); VerticalMarginBox.Text = settings.VerticalMarginDip.ToString(CultureInfo.CurrentCulture);
        ScaleBox.Text = settings.UiScalePercent.ToString(CultureInfo.CurrentCulture); ExecutableBox.Text = settings.CodexExecutablePath ?? string.Empty;
        RefreshBox.Text = settings.RefreshIntervalSeconds.ToString(CultureInfo.CurrentCulture); StartupTimeoutBox.Text = settings.StartupTimeoutSeconds.ToString(CultureInfo.CurrentCulture);
        PresetPlacementBox.IsChecked = settings.PlacementMode == PlacementMode.Preset;
        CustomPlacementBox.IsChecked = settings.PlacementMode == PlacementMode.Custom;
        PresetPlacementBox.Checked += SelectPresetPlacement;
        CustomPlacementBox.Checked += SelectCustomPlacement;
        MonitorBox.SelectionChanged += SelectPresetPlacement;
        AnchorBox.SelectionChanged += SelectPresetPlacement;
        HorizontalMarginBox.TextChanged += SelectPresetPlacement;
        VerticalMarginBox.TextChanged += SelectPresetPlacement;
        ScaleBox.TextChanged += ClearValidation;
        HorizontalMarginBox.TextChanged += ClearValidation;
        VerticalMarginBox.TextChanged += ClearValidation;
        RefreshBox.TextChanged += ClearValidation;
        StartupTimeoutBox.TextChanged += ClearValidation;
        SetPlacementMode(settings.PlacementMode);
        RefreshClaudeStatus();
        _claudeStatusTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => RefreshClaudeStatus(),
            Dispatcher);
        _claudeStatusTimer.Start();
        Closed += (_, _) => _claudeStatusTimer.Stop();
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (!TryReadControls(_settings, out AppSettings controls)) return;
        var resolver = new CodexHomePathResolver(
            Environment.GetEnvironmentVariable("CODEX_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        IReadOnlyList<SettingsValidationError> errors =
            AppSettingsValidator.ValidateForSave(controls, resolver);
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
        DialogResult = true;
    }

    private void SaveCurrentPosition(object sender, RoutedEventArgs e)
    {
        AppSettings captured = _captureCurrentPosition();
        _settings = PlacementSettingsComposer.UseCapturedPosition(ReadControlsLenient(_settings), captured);
        SetPlacementMode(PlacementMode.Custom);
        _suppressPlacementModeChange = true;
        SelectMonitor(captured.MonitorDeviceName);
        _suppressPlacementModeChange = false;
    }

    private void ResetPosition(object sender, RoutedEventArgs e)
    {
        SetPlacementMode(PlacementMode.Preset);
        _settings = PlacementSettingsComposer.UsePreset(ReadControlsLenient(_settings) with
        {
            MonitorDeviceName = null,
            Anchor = PlacementAnchor.TopRight,
            HorizontalMarginDip = 12,
            VerticalMarginDip = 12,
        });
        MonitorBox.SelectedIndex = 0;
        SelectAnchor(PlacementAnchor.TopRight);
        HorizontalMarginBox.Text = "12";
        VerticalMarginBox.Text = "12";
    }

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

        controls = basis with
        {
            ClickThrough = ClickThroughBox.IsChecked == true,
            AlwaysOnTop = TopmostBox.IsChecked == true,
            StartWithWindows = StartupBox.IsChecked == true,
            DisplayMode = DisplayModeBox.SelectedItem is DisplayModeChoice displayMode
                ? displayMode.Value
                : WidgetDisplayMode.Standard,
            MonitorDeviceName = (MonitorBox.SelectedItem as MonitorChoice)?.DeviceName,
            Anchor = AnchorBox.SelectedItem is AnchorChoice anchor ? anchor.Value : PlacementAnchor.TopRight,
            HorizontalMarginDip = horizontalMargin,
            VerticalMarginDip = verticalMargin,
            UiScalePercent = scale,
            CodexExecutablePath = ExecutableBox.Text,
            RefreshIntervalSeconds = refreshInterval,
            StartupTimeoutSeconds = startupTimeout,
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
            MutedColor = MutedColorBox.Text,
            AccentColor = AccentColorBox.Text,
            WarningColor = WarningColorBox.Text,
            DangerColor = DangerColorBox.Text,
            BackgroundEnabled = BackgroundEnabledBox.IsChecked == true,
            HideBackgroundBehindWindows = HideBackgroundBox.IsChecked == true,
            BackgroundColor = BackgroundColorBox.Text,
            BackgroundOpacity = backgroundOpacityPercent / 100d,
            BackgroundFillMode = BackgroundFillModeBox.SelectedItem is BackgroundFillModeChoice fillMode
                ? fillMode.Value
                : BackgroundFillMode.Solid,
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
        MutedColor = MutedColorBox.Text,
        AccentColor = AccentColorBox.Text,
        WarningColor = WarningColorBox.Text,
        DangerColor = DangerColorBox.Text,
        BackgroundEnabled = BackgroundEnabledBox.IsChecked == true,
        HideBackgroundBehindWindows = HideBackgroundBox.IsChecked == true,
        BackgroundColor = BackgroundColorBox.Text,
        BackgroundOpacity = ParseDoubleOrFallback(BackgroundOpacityBox.Text, basis.BackgroundOpacity * 100d) / 100d,
        BackgroundFillMode = BackgroundFillModeBox.SelectedItem is BackgroundFillModeChoice fillMode
            ? fillMode.Value
            : BackgroundFillMode.Solid,
        BackgroundEdgeFadePercent = ParseDoubleOrFallback(BackgroundFadeBox.Text, basis.BackgroundEdgeFadePercent),
    };

    private void AddCodexAccount(object sender, RoutedEventArgs e)
    {
        string id = Guid.NewGuid().ToString("N");
        var account = new CodexAccountEditor(new()
        {
            Id = id,
            DisplayName = $"CODEX {_codexAccounts.Count + 1}",
            Enabled = true,
            ShowInWidget = true,
        });
        _codexAccounts.Add(account);
        CodexAccountsGrid.SelectedItem = account;
        CodexAccountsGrid.ScrollIntoView(account);
    }

    private void RemoveCodexAccount(object sender, RoutedEventArgs e)
    {
        if (CodexAccountsGrid.SelectedItem is not CodexAccountEditor selected) return;
        if (string.Equals(selected.Id, CodexAccountSettings.DefaultAccountId, StringComparison.OrdinalIgnoreCase))
        {
            ValidationMessage.Text = "既定アカウントは削除できません。";
            return;
        }
        _codexAccounts.Remove(selected);
    }

    private void SelectCodexHome(object sender, RoutedEventArgs e)
    {
        if (CodexAccountsGrid.SelectedItem is not CodexAccountEditor selected)
        {
            ValidationMessage.Text = "追加アカウントを選択してください。";
            return;
        }
        if (selected.IsDefault)
        {
            ValidationMessage.Text = "既定アカウントは現在のCodex環境を継承します。";
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "公式Codex CLIでサインイン済みのCODEX_HOMEを選択してください。",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(selected.CodexHomePath)
                ? selected.CodexHomePath
                : string.Empty,
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            selected.CodexHomePath = dialog.SelectedPath;
            ValidationMessage.Text = string.Empty;
        }
    }

    private void FocusCodexValidation(SettingsValidationError error)
    {
        CodexAccountEditor? editor = _codexAccounts.FirstOrDefault(account =>
            string.Equals(account.Id, error.AccountId, StringComparison.OrdinalIgnoreCase));
        if (editor is not null)
        {
            CodexAccountsGrid.SelectedItem = editor;
            int columnIndex = error.Field switch
            {
                nameof(CodexAccountSettings.DisplayName) => 0,
                nameof(CodexAccountSettings.CodexHomePath) => 1,
                _ => 0,
            };
            Controls.DataGridColumn column = CodexAccountsGrid.Columns[columnIndex];
            CodexAccountsGrid.CurrentCell = new(editor, column);
            CodexAccountsGrid.ScrollIntoView(editor, column);
            Dispatcher.BeginInvoke(() =>
            {
                CodexAccountsGrid.Focus();
                CodexAccountsGrid.BeginEdit();
            });
        }
        else
        {
            CodexAccountsGrid.Focus();
        }
    }

    private bool TryReadDouble(
        Controls.TextBox textBox,
        string message,
        double minimum,
        double maximum,
        int tabIndex,
        out double value)
    {
        if (double.TryParse(
                textBox.Text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out value) &&
            double.IsFinite(value) &&
            value >= minimum &&
            value <= maximum)
        {
            return true;
        }

        ShowValidation(message, tabIndex, textBox);
        return false;
    }

    private bool TryReadInt(
        Controls.TextBox textBox,
        string message,
        int minimum,
        int maximum,
        int tabIndex,
        out int value)
    {
        if (int.TryParse(
                textBox.Text,
                NumberStyles.Integer,
                CultureInfo.CurrentCulture,
                out value) &&
            value >= minimum &&
            value <= maximum)
        {
            return true;
        }

        ShowValidation(message, tabIndex, textBox);
        return false;
    }

    private void ShowValidation(string message, int tabIndex, Controls.TextBox textBox)
    {
        CategoryTabs.SelectedIndex = tabIndex;
        ValidationMessage.Text = message;
        Dispatcher.BeginInvoke(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }

    private void FocusAppearanceValidation(AppearanceValidationError error)
    {
        Controls.TextBox? textBox = error.Field switch
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
        ValidationMessage.Text = error.Message;
        if (textBox is not null)
        {
            Dispatcher.BeginInvoke(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            });
        }
    }

    private void AppearanceControlChanged(object? sender, RoutedEventArgs e)
    {
        UpdateAppearanceControlState();
        ClearValidation(sender, e);
    }

    private void UpdateAppearanceControlState()
    {
        bool enabled = BackgroundEnabledBox.IsChecked == true;
        BackgroundColorBox.IsEnabled = enabled;
        BackgroundOpacityBox.IsEnabled = enabled;
        BackgroundFillModeBox.IsEnabled = enabled;
        bool edgeFade = enabled && BackgroundFillModeBox.SelectedItem is BackgroundFillModeChoice choice
            && choice.Value == BackgroundFillMode.EdgeFade;
        BackgroundFadeBox.IsEnabled = edgeFade;
        HideBackgroundBox.IsEnabled = enabled && TopmostBox.IsChecked == true;

        if (AppearanceSettingsValidator.TryNormalizeColor(BackgroundColorBox.Text, out string normalized))
        {
            System.Windows.Media.Color color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(normalized);
            BackgroundPreview.Background = new System.Windows.Media.SolidColorBrush(color);
        }
        else
        {
            BackgroundPreview.Background = System.Windows.Media.Brushes.Transparent;
        }
        BackgroundPreview.Opacity = enabled
            ? Math.Clamp(ParseDoubleOrFallback(BackgroundOpacityBox.Text, 69) / 100d, 0, 1)
            : 0;
    }

    private void SelectForegroundColor(object sender, RoutedEventArgs e) => SelectColor(ForegroundColorBox);
    private void SelectMutedColor(object sender, RoutedEventArgs e) => SelectColor(MutedColorBox);
    private void SelectAccentColor(object sender, RoutedEventArgs e) => SelectColor(AccentColorBox);
    private void SelectWarningColor(object sender, RoutedEventArgs e) => SelectColor(WarningColorBox);
    private void SelectDangerColor(object sender, RoutedEventArgs e) => SelectColor(DangerColorBox);
    private void SelectBackgroundColor(object sender, RoutedEventArgs e) => SelectColor(BackgroundColorBox);

    private static void SelectColor(Controls.TextBox target)
    {
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = TryGetDrawingColor(target.Text) ?? System.Drawing.Color.Black,
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            target.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
    }

    private static System.Drawing.Color? TryGetDrawingColor(string? value)
    {
        if (!AppearanceSettingsValidator.TryNormalizeColor(value, out string normalized)) return null;
        string hex = normalized[3..];
        return System.Drawing.Color.FromArgb(
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16));
    }

    private void ClearValidation(object? sender, EventArgs e)
    {
        ValidationMessage.Foreground = System.Windows.Media.Brushes.Crimson;
        ValidationMessage.Text = string.Empty;
    }

    private void CopyClaudeExample(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(ClaudeExampleBox.Text);
        ValidationMessage.Foreground = System.Windows.Media.Brushes.DodgerBlue;
        ValidationMessage.Text = "Claude Code設定をクリップボードへコピーしました。";
    }

    private void OpenClaudeSetup(object sender, RoutedEventArgs e)
    {
        ShowClaudeBox.IsChecked = true;
        ClaudeSetupRequested?.Invoke();
    }

    private void RefreshClaudeStatus()
    {
        ClaudeConnectionStatusText.Text = _claudeStatusProvider is null
            ? "接続状態は、アプリを起動しているときに確認できます。"
            : UsageStatusFormatter.FormatClaudeConnection(_claudeStatusProvider());
        string? source = _claudeSourceProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(source))
            ClaudeConnectionStatusText.Text += Environment.NewLine + "現在表示元: " + source;
        if (_claudeHealthProvider is { } healthProvider)
        {
            ClaudeRuntimeHealth health = healthProvider();
            ClaudeConnectionStatusText.Text += Environment.NewLine +
                "active最終試行: " + FormatChannelTime(health.ActiveLastAttemptAt);
            if (!string.IsNullOrWhiteSpace(health.ActiveFailureReason))
                ClaudeConnectionStatusText.Text +=
                    $"（失敗: {health.ActiveFailureReason}）";
            ClaudeConnectionStatusText.Text += Environment.NewLine +
                "statusLine最終受信: " + FormatChannelTime(health.PassiveLastReceivedAt);
        }
    }

    private static string FormatChannelTime(DateTimeOffset? value) =>
        value is null
            ? "なし"
            : value.Value.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture);

    private void SelectPresetPlacement(object? sender, EventArgs e)
    {
        if (!_suppressPlacementModeChange) SetPlacementMode(PlacementMode.Preset);
    }

    private void SelectCustomPlacement(object? sender, EventArgs e)
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

    private void SelectMonitor(string? deviceName)
    {
        MonitorBox.SelectedItem = MonitorBox.Items
            .Cast<MonitorChoice>()
            .FirstOrDefault(item => string.Equals(
                item.DeviceName,
                deviceName,
                StringComparison.OrdinalIgnoreCase)) ?? MonitorBox.Items[0];
    }

    private void SelectAnchor(PlacementAnchor anchor)
    {
        AnchorBox.SelectedItem = AnchorBox.Items
            .Cast<AnchorChoice>()
            .First(item => item.Value == anchor);
    }

    private static double ParseDoubleOrFallback(string value, double fallback)
    {
        return double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out double parsed) &&
            double.IsFinite(parsed)
            ? parsed
            : fallback;
    }

    private static int ParseIntOrFallback(string value, int fallback)
    {
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.CurrentCulture,
            out int parsed)
            ? parsed
            : fallback;
    }

    private sealed record MonitorChoice(string? DeviceName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record AnchorChoice(PlacementAnchor Value, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record ClaudeModeChoice(ClaudeUsageAcquisitionMode Value, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record DisplayModeChoice(WidgetDisplayMode Value, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed record BackgroundFillModeChoice(BackgroundFillMode Value, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }

    private sealed class CodexAccountEditor : INotifyPropertyChanged
    {
        private string _displayName;
        private string? _codexHomePath;
        private bool _enabled;
        private bool _showInWidget;

        public CodexAccountEditor(CodexAccountSettings settings)
        {
            Id = settings.Id;
            _displayName = settings.DisplayName;
            _codexHomePath = settings.CodexHomePath;
            _enabled = settings.Enabled;
            _showInWidget = settings.ShowInWidget;
        }

        public string Id { get; }
        public bool IsDefault => string.Equals(
            Id,
            CodexAccountSettings.DefaultAccountId,
            StringComparison.OrdinalIgnoreCase);
        public string DisplayName
        {
            get => _displayName;
            set => SetField(ref _displayName, value);
        }
        public string? CodexHomePath
        {
            get => _codexHomePath;
            set => SetField(ref _codexHomePath, value);
        }
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }
        public bool ShowInWidget
        {
            get => _showInWidget;
            set => SetField(ref _showInWidget, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public CodexAccountSettings ToSettings() => new()
        {
            Id = Id,
            DisplayName = DisplayName,
            CodexHomePath = CodexHomePath,
            Enabled = Enabled,
            ShowInWidget = ShowInWidget,
        };

        private void SetField<T>(
            ref T field,
            T value,
            [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new(propertyName));
        }
    }
}
