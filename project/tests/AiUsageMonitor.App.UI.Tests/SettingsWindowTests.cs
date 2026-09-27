using System.Globalization;
using AiUsageMonitor.App.UI.Runtime;
using AiUsageMonitor.App.UI.Settings;
using AiUsageMonitor.App.UI.Views;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using static AiUsageMonitor.App.UI.Tests.MainWindowTestSupport;
using PlacementMode = AiUsageMonitor.Core.Settings.PlacementMode;

namespace AiUsageMonitor.App.UI.Tests;

// WPF版SettingsWindowTestsの意図をAvaloniaへ移植したもの。
public sealed class SettingsWindowTests
{
    [AvaloniaTheory]
    [InlineData("ja-JP", "125.5")]
    [InlineData("de-DE", "125,5")]
    public void NonIntegerScaleRoundTripsUnderCulture(string cultureName, string expectedDisplay)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(cultureName);
            SettingsWindow window = CreateWindow(new AppSettings { UiScalePercent = 125.5 });
            Assert.Equal(expectedDisplay, window.ScaleBox.Text);

            window.Save();

            Assert.True(window.Saved);
            Assert.Equal(125.5, window.Result.UiScalePercent);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [AvaloniaFact]
    public void UsesExpectedCategoriesAndResizableShell()
    {
        SettingsWindow window = CreateWindow();
        try
        {
            string[] headers = window.CategoryTabs.Items.OfType<TabItem>()
                .Select(item => Assert.IsType<string>(item.Header))
                .ToArray();

            Assert.Equal(["全体設定", "表示位置", "外観", "CODEX", "CLAUDE CODE"], headers);
            Assert.True(window.CanResize);
            Assert.False(window.ShowInTaskbar);
            Assert.True(window.SaveButton.IsDefault);
            Assert.Equal(2, window.DisplayModeBox.ItemCount);
            Assert.Equal(0, window.DisplayModeBox.SelectedIndex);
            Assert.Equal(OperatingSystem.IsWindows() ? "Windowsログオン時に自動起動する" : "ログイン時に自動起動する", window.StartupBox.Content);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PreservesCompactDisplayModeThroughSave()
    {
        SettingsWindow window = CreateWindow(new AppSettings { DisplayMode = WidgetDisplayMode.Compact });
        Assert.Equal(1, window.DisplayModeBox.SelectedIndex);

        window.Save();

        Assert.Equal(WidgetDisplayMode.Compact, window.Result.DisplayMode);
    }

    [AvaloniaFact]
    public void FieldsDoNotOverlapAtMinimumSize()
    {
        SettingsWindow window = CreateWindow();
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        string[][] pages =
        [
            ["ScaleBox"],
            ["HorizontalMarginBox", "VerticalMarginBox"],
            ["FontFamilyBox", "ForegroundColorBox", "MutedColorBox", "AccentColorBox", "WarningColorBox", "DangerColorBox", "BackgroundColorBox", "BackgroundOpacityBox", "BackgroundFadeBox"],
            ["ExecutableBox", "RefreshBox", "StartupTimeoutBox"],
            ["ClaudeExecutableBox"],
        ];
        window.Show();
        try
        {
            for (int page = 0; page < pages.Length; page++)
            {
                window.CategoryTabs.SelectedIndex = page;
                Settle(window);
                Rect[] boxes = pages[page]
                    .Select(name => Named<TextBox>(window, name))
                    .Select(box => BoundsIn(box, window))
                    .OrderBy(bounds => bounds.Top)
                    .ToArray();
                Assert.All(boxes, bounds => Assert.True(bounds.Width > 0 && bounds.Height > 0));
                for (int index = 1; index < boxes.Length; index++)
                    Assert.True(boxes[index - 1].Bottom <= boxes[index].Top, $"Settings page {page}, fields {index - 1} and {index} overlap.");
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SavesValidValues()
    {
        SettingsWindow window = CreateWindow();
        window.ScaleBox.Text = "125";
        window.HorizontalMarginBox.Text = "20";
        window.VerticalMarginBox.Text = "30";
        window.RefreshBox.Text = "120";
        window.StartupTimeoutBox.Text = "45";
        window.ShowCodexBox.IsChecked = false;
        window.ShowAdditionalUsageBox.IsChecked = true;
        window.ShowCreditsBox.IsChecked = true;
        window.ShowClaudeBox.IsChecked = true;
        window.BackgroundEnabledBox.IsChecked = true;
        window.BackgroundFillModeBox.SelectedIndex = 1;
        window.BackgroundFadeBox.Text = "30";

        window.Save();

        Assert.True(window.Saved);
        Assert.Equal(125, window.Result.UiScalePercent);
        Assert.Equal(20, window.Result.HorizontalMarginDip);
        Assert.Equal(30, window.Result.VerticalMarginDip);
        Assert.Equal(120, window.Result.RefreshIntervalSeconds);
        Assert.Equal(45, window.Result.StartupTimeoutSeconds);
        Assert.False(window.Result.ShowCodexUsage);
        Assert.True(window.Result.ShowAdditionalUsage);
        Assert.True(window.Result.ShowCredits);
        Assert.True(window.Result.CodexMonetarySettingsInitialized);
        Assert.True(window.Result.ShowClaudeUsage);
        Assert.Equal(BackgroundFillMode.EdgeFade, window.Result.BackgroundFillMode);
        Assert.Equal(30, window.Result.BackgroundEdgeFadePercent);
    }

    [AvaloniaFact]
    public void CancelKeepsOriginalAccountSettings()
    {
        AppSettings settings = new AppSettings
        {
            CodexAccounts = [CodexAccountSettings.Default with { DisplayName = "Original" }],
        }.Normalized();
        var window = new SettingsWindow(settings, () => settings);
        Assert.Single(window.CodexAccountEditors).DisplayName = "Edited";

        window.Close();

        Assert.False(window.Saved);
        Assert.Equal("Original", Assert.Single(window.Result.CodexAccounts).DisplayName);
    }

    [AvaloniaFact]
    public void InvalidAccountSelectsItsHomeField()
    {
        string missingHome = Path.Combine(Path.GetTempPath(), "codex-monitor-missing-" + Guid.NewGuid().ToString("N"));
        AppSettings settings = new AppSettings
        {
            CodexAccounts =
            [
                CodexAccountSettings.Default,
                new() { Id = "missing", DisplayName = "Missing", CodexHomePath = missingHome },
            ],
        }.Normalized();
        var window = new SettingsWindow(settings, () => settings);
        window.Show();
        try
        {
            window.Save();
            Settle(window);

            Assert.False(window.Saved);
            Assert.Equal(SettingsWindow.CodexTabIndex, window.CategoryTabs.SelectedIndex);
            Assert.Equal(1, window.CodexAccountsList.SelectedIndex);
            Assert.Equal(nameof(CodexAccountSettings.CodexHomePath), window.LastAccountFocusField);
            Assert.Contains("CODEX_HOME", window.ValidationMessage.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ShowsWaitingStatusAndSeparatesSelectedSourceAndChannelHealth()
    {
        AppSettings settings = new AppSettings { ShowClaudeUsage = true, ClaudeSetupCompleted = true }.Normalized();
        UsageSnapshot waiting = UsageSnapshot.Loading(UsageProvider.Claude, DateTimeOffset.UtcNow) with
        {
            Availability = UsageAvailability.Waiting,
        };
        var waitingWindow = new SettingsWindow(settings, () => settings, () => waiting);
        DateTimeOffset activeAttempt = new(2026, 7, 27, 3, 10, 0, TimeSpan.Zero);
        DateTimeOffset passiveReceipt = new(2026, 7, 27, 3, 11, 0, TimeSpan.Zero);
        var healthWindow = new SettingsWindow(
            settings,
            () => settings,
            () => UsageSnapshot.Loading(UsageProvider.Claude, activeAttempt),
            () => "Claude Code statusLine（常駐セッション）",
            () => new(ClaudeUsageSourceKind.StatusLinePassive, activeAttempt, "TIMEOUT", passiveReceipt));
        var withoutRuntime = new SettingsWindow(settings, () => settings);
        try
        {
            Assert.Equal("接続済みです。最初の利用情報を待っています。", waitingWindow.ClaudeConnectionStatusText.Text);
            string status = healthWindow.ClaudeConnectionStatusText.Text!;
            Assert.Contains("現在表示元: Claude Code statusLine", status, StringComparison.Ordinal);
            Assert.Contains("active最終試行:", status, StringComparison.Ordinal);
            Assert.Contains("失敗: TIMEOUT", status, StringComparison.Ordinal);
            Assert.Contains("statusLine最終受信:", status, StringComparison.Ordinal);
            Assert.Contains("アプリを起動しているとき", withoutRuntime.ClaudeConnectionStatusText.Text, StringComparison.Ordinal);
        }
        finally
        {
            waitingWindow.Close();
            healthWindow.Close();
            withoutRuntime.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("ScaleBox", "74", SettingsWindow.GeneralTabIndex, "表示倍率")]
    [InlineData("ScaleBox", "201", SettingsWindow.GeneralTabIndex, "表示倍率")]
    [InlineData("HorizontalMarginBox", "-1", SettingsWindow.PlacementTabIndex, "水平余白")]
    [InlineData("VerticalMarginBox", "NaN", SettingsWindow.PlacementTabIndex, "垂直余白")]
    [InlineData("BackgroundOpacityBox", "101", SettingsWindow.AppearanceTabIndex, "背景の不透明度")]
    [InlineData("BackgroundFadeBox", "4", SettingsWindow.AppearanceTabIndex, "フェード幅")]
    [InlineData("RefreshBox", "59", SettingsWindow.CodexTabIndex, "更新間隔")]
    [InlineData("StartupTimeoutBox", "121", SettingsWindow.CodexTabIndex, "起動待ち")]
    public void RejectsInvalidValues(string fieldName, string value, int expectedTab, string expectedMessage)
    {
        SettingsWindow window = CreateWindow();
        try
        {
            window.FindControl<TextBox>(fieldName)!.Text = value;
            window.Save();

            Assert.False(window.Saved);
            Assert.Equal(expectedTab, window.CategoryTabs.SelectedIndex);
            Assert.StartsWith(expectedMessage, window.ValidationMessage.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClearsValidationAfterInputChanges()
    {
        SettingsWindow window = CreateWindow();
        try
        {
            window.DisplayModeBox.SelectedIndex = 1;
            window.ScaleBox.Text = "invalid";
            window.Save();
            Assert.NotEmpty(window.ValidationMessage.Text!);
            Assert.Equal(1, window.DisplayModeBox.SelectedIndex);

            window.ScaleBox.Text = "100";
            Assert.Empty(window.ValidationMessage.Text!);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CaptureAndResetPlacementPreserveEditedFields()
    {
        AppSettings current = new AppSettings().Normalized();
        AppSettings captured = current with
        {
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = 0.25,
            CustomTopFraction = 0.35,
        };
        var window = new SettingsWindow(current, () => captured);
        try
        {
            window.ScaleBox.Text = "125";
            window.RefreshBox.Text = "180";
            window.SaveCurrentPosition();
            Assert.Equal(PlacementMode.Custom, window.Result.PlacementMode);
            Assert.Equal(0.25, window.Result.CustomLeftFraction);
            Assert.Equal(125, window.Result.UiScalePercent);
            Assert.Equal(180, window.Result.RefreshIntervalSeconds);
            Assert.True(window.CustomPlacementBox.IsChecked);
            Assert.Contains("自由配置", window.PlacementStatusText.Text, StringComparison.Ordinal);

            window.ResetPosition();
            Assert.Equal(PlacementMode.Preset, window.Result.PlacementMode);
            Assert.Equal(PlacementAnchor.TopRight, window.Result.Anchor);
            Assert.Equal("12", window.HorizontalMarginBox.Text);
            Assert.Equal(125, window.Result.UiScalePercent);
            Assert.True(window.PresetPlacementBox.IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ChangingPresetFieldsSwitchesPlacementModeBackToPreset()
    {
        var window = CreateWindow(new AppSettings { PlacementMode = PlacementMode.Custom, CustomLeftFraction = .1, CustomTopFraction = .1 });
        try
        {
            Assert.True(window.CustomPlacementBox.IsChecked);
            window.HorizontalMarginBox.Text = "40";
            Assert.True(window.PresetPlacementBox.IsChecked);
            window.CustomPlacementBox.IsChecked = true;
            window.Save();
            Assert.Equal(PlacementMode.Custom, window.Result.PlacementMode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void OpeningWithCustomPlacementStaysCustomAfterPendingEventsRun()
    {
        // AvaloniaのTextChangedは遅れて届く。初期値の設定が配置モードや検証表示を変えないことを確認する。
        SettingsWindow window = CreateWindow(new AppSettings
        {
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = .2,
            CustomTopFraction = .3,
        });
        window.Show();
        try
        {
            Settle(window);
            Assert.True(window.CustomPlacementBox.IsChecked);
            Assert.False(window.PresetPlacementBox.IsChecked);

            window.ScaleBox.Text = "invalid";
            window.Save();
            Settle(window);
            Assert.StartsWith("表示倍率", window.ValidationMessage.Text, StringComparison.Ordinal);

            window.ScaleBox.Text = "100";
            window.Save();
            Assert.Equal(PlacementMode.Custom, window.Result.PlacementMode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AddedAccountCanBeRemovedButDefaultCannot()
    {
        SettingsWindow window = CreateWindow();
        window.Show();
        try
        {
            int initial = window.CodexAccountEditors.Count;
            window.AddCodexAccount();
            Assert.Equal(initial + 1, window.CodexAccountEditors.Count);
            Assert.Equal($"CODEX {initial + 1}", window.CodexAccountEditors[^1].DisplayName);
            window.RemoveCodexAccount();
            Assert.Equal(initial, window.CodexAccountEditors.Count);

            window.CodexAccountsList.SelectedIndex = 0;
            window.RemoveCodexAccount();
            Assert.Equal(initial, window.CodexAccountEditors.Count);
            Assert.Contains("削除できません", window.ValidationMessage.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("FontFamilyBox", "bad\nfont", "フォント")]
    [InlineData("ForegroundColorBox", "red", "色")]
    [InlineData("MutedColorBox", "#GGGGGG", "色")]
    [InlineData("AccentColorBox", "#12345", "色")]
    [InlineData("WarningColorBox", "invalid", "色")]
    [InlineData("DangerColorBox", "invalid", "色")]
    [InlineData("BackgroundColorBox", "invalid", "色")]
    public void InvalidAppearanceSelectsAppearanceTabAndExplainsField(string fieldName, string value, string message)
    {
        SettingsWindow window = CreateWindow();
        try
        {
            window.FindControl<TextBox>(fieldName)!.Text = value;
            window.Save();

            Assert.False(window.Saved);
            Assert.Equal(SettingsWindow.AppearanceTabIndex, window.CategoryTabs.SelectedIndex);
            Assert.Contains(message, window.ValidationMessage.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CodexHomeSelectionNeedsASelectedNonDefaultAccount()
    {
        SettingsWindow window = CreateWindow();
        window.Show();
        try
        {
            window.CodexAccountsList.SelectedItem = null;
            Assert.Null(window.AccountForHomeSelection());
            Assert.Contains("選択してください", window.ValidationMessage.Text, StringComparison.Ordinal);

            window.CodexAccountsList.SelectedIndex = 0;
            Assert.Null(window.AccountForHomeSelection());
            Assert.Contains("継承", window.ValidationMessage.Text, StringComparison.Ordinal);

            window.AddCodexAccount();
            Assert.Same(window.CodexAccountEditors[^1], window.AccountForHomeSelection());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("#123456", 0x12, 0x34, 0x56)]
    [InlineData("#AA123456", 0x12, 0x34, 0x56)]
    public void ColorPickerUsesOpaqueConfiguredColor(string configured, byte red, byte green, byte blue)
    {
        Assert.Equal(Color.FromRgb(red, green, blue), SettingsWindow.TryGetOpaqueColor(configured));
        Assert.Null(SettingsWindow.TryGetOpaqueColor("invalid"));
        Assert.Equal("#123456", SettingsWindow.FormatOpaqueColor(Color.FromArgb(0x80, 0x12, 0x34, 0x56)));
    }

    [AvaloniaFact]
    public void BackgroundControlsFollowEnabledFillModeAndTopmost()
    {
        SettingsWindow window = CreateWindow(new AppSettings { AlwaysOnTop = true });
        try
        {
            Assert.False(window.BackgroundColorBox.IsEnabled);
            Assert.False(window.HideBackgroundBox.IsEnabled);
            Assert.Equal(0, window.BackgroundPreview.Opacity);

            window.BackgroundEnabledBox.IsChecked = true;
            window.BackgroundColorBox.Text = "#102030";
            window.BackgroundOpacityBox.Text = "50";
            Assert.True(window.BackgroundColorBox.IsEnabled);
            Assert.False(window.BackgroundFadeBox.IsEnabled);
            Assert.True(window.HideBackgroundBox.IsEnabled);
            Assert.Equal(0.5, window.BackgroundPreview.Opacity, 3);
            Assert.Equal(Color.Parse("#FF102030"), ((ISolidColorBrush)window.BackgroundPreview.Background!).Color);

            window.BackgroundFillModeBox.SelectedIndex = 1;
            Assert.True(window.BackgroundFadeBox.IsEnabled);
            window.TopmostBox.IsChecked = false;
            Assert.False(window.HideBackgroundBox.IsEnabled);
            window.BackgroundColorBox.Text = "invalid";
            Assert.Same(Brushes.Transparent, window.BackgroundPreview.Background);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SetupActionEnablesClaudeAndRaisesRequest()
    {
        SettingsWindow window = CreateWindow(new AppSettings { ShowClaudeUsage = false });
        bool requested = false;
        window.ClaudeSetupRequested += () => requested = true;
        try
        {
            window.RequestClaudeSetup();
            Assert.True(window.ShowClaudeBox.IsChecked);
            Assert.True(requested);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void MonitorListUsesHostProvidedMonitorsAndKeepsDisconnectedSelection()
    {
        MonitorChoice primary = MonitorChoice.ForScreen("Built-in", "stable-1", primary: true);
        var window = new SettingsWindow(
            new AppSettings { MonitorDeviceName = "External", MonitorStableId = "stable-2" }.Normalized(),
            () => new AppSettings(),
            connectedMonitors: [primary],
            claudeSetupExample: "example");
        try
        {
            Assert.Equal(3, window.MonitorBox.ItemCount);
            var selected = Assert.IsType<MonitorChoice>(window.MonitorBox.SelectedItem);
            Assert.True(selected.IsDisconnected);
            Assert.Equal("example", window.ClaudeExampleBox.Text);

            window.Save();
            Assert.Equal("stable-2", window.Result.MonitorStableId);
        }
        finally
        {
            window.Close();
        }
    }

    private static SettingsWindow CreateWindow(AppSettings? settings = null)
    {
        AppSettings current = (settings ?? new AppSettings()).Normalized();
        return new SettingsWindow(current, () => current, claudeSetupExample: "{}");
    }
}
