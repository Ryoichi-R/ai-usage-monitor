using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Claude.Acquisition;

namespace AiUsageMonitor.App.Tests;

public sealed class SettingsWindowTests
{
    // 非整数倍率(125.5)の文化依存入力契約を固定する回帰テスト。既存不具合の修正ではなく、
    // 表示・解析が同一cultureで往復することを将来にわたり保証する。CurrentCultureはSTAスレッド
    // ローカルに差し替え、finallyで復元するため他テストへ影響しない。
    [Theory]
    [InlineData("ja-JP", "125.5")]
    [InlineData("de-DE", "125,5")]
    public void SettingsWindow_NonIntegerScaleRoundTripsUnderCulture(string cultureName, string expectedDisplay)
    {
        RunInSta(() =>
        {
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
                var settings = new AppSettings { UiScalePercent = 125.5 }.Normalized();
                var window = new SettingsWindow(settings, () => settings);

                Assert.Equal(expectedDisplay, window.ScaleBox.Text);

                window.Loaded += (_, _) => window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(window.ShowDialog());
                Assert.Equal(125.5, window.Result.UiScalePercent);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        });
    }

    [Fact]
    public void SettingsWindow_UsesExpectedCategoriesAndResizableShell()
    {
        RunInSta(() =>
        {
            var window = CreateWindow();

            string[] headers = window.CategoryTabs.Items
                .Cast<TabItem>()
                .Select(item => Assert.IsType<string>(item.Header))
                .ToArray();

            Assert.Equal(["全体設定", "表示位置", "外観", "CODEX", "CLAUDE CODE"], headers);
            Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
            Assert.False(window.ShowInTaskbar);
            Assert.True(window.SaveButton.IsDefault);
            Assert.NotNull(window.ValidationMessage);
            Assert.Equal(2, window.DisplayModeBox.Items.Count);
            Assert.Equal(0, window.DisplayModeBox.SelectedIndex);
        });
    }

    [Fact]
    public void SettingsWindow_PreservesCompactDisplayModeThroughSave()
    {
        RunInSta(() =>
        {
            var window = CreateWindow(new AppSettings { DisplayMode = WidgetDisplayMode.Compact });
            Assert.Equal(1, window.DisplayModeBox.SelectedIndex);
            window.DisplayModeBox.SelectedIndex = 1;
            window.Loaded += (_, _) => window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(window.ShowDialog());
            Assert.Equal(WidgetDisplayMode.Compact, window.Result.DisplayMode);
        });
    }

    [Fact]
    public void SettingsWindow_FieldsDoNotOverlapAtMinimumSize()
    {
        RunInSta(() =>
        {
            var window = CreateWindow();
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
                    Layout(window);
                    Rect[] boxes = pages[page]
                        .Select(name => Assert.IsType<TextBox>(window.FindName(name)))
                        .Select(box => box.TransformToAncestor(window).TransformBounds(new Rect(box.RenderSize)))
                        .OrderBy(bounds => bounds.Top)
                        .ToArray();
                    for (int index = 1; index < boxes.Length; index++)
                    {
                        Assert.True(
                            boxes[index - 1].Bottom <= boxes[index].Top,
                            $"Settings page {page}, fields {index - 1} and {index} overlap.");
                    }
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SettingsWindow_SavesValidValues()
    {
        RunInSta(() =>
        {
            var window = CreateWindow();
            window.ScaleBox.Text = "125";
            window.HorizontalMarginBox.Text = "20";
            window.VerticalMarginBox.Text = "30";
            window.RefreshBox.Text = "120";
            window.StartupTimeoutBox.Text = "45";
            window.ShowCodexBox.IsChecked = false;
            window.ShowAdditionalUsageBox.IsChecked = true;
            window.ShowCreditsBox.IsChecked = true;
            window.ShowClaudeBox.IsChecked = true;
            window.Loaded += (_, _) => window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.True(window.ShowDialog());
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
        });
    }

    [Fact]
    public void SettingsWindow_CancelKeepsOriginalAccountSettings()
    {
        RunInSta(() =>
        {
            AppSettings settings = new AppSettings
            {
                CodexAccounts =
                [
                    CodexAccountSettings.Default with { DisplayName = "Original" },
                ],
            }.Normalized();
            var window = new SettingsWindow(settings, () => settings);
            object editor = Assert.Single(window.CodexAccountsGrid.Items.Cast<object>());
            editor.GetType().GetProperty(nameof(CodexAccountSettings.DisplayName))!
                .SetValue(editor, "Edited");

            window.Close();

            Assert.Equal("Original", Assert.Single(window.Result.CodexAccounts).DisplayName);
        });
    }

    [Fact]
    public void SettingsWindow_InvalidAccountSelectsItsHomeCell()
    {
        RunInSta(() =>
        {
            string missingHome = Path.Combine(
                Path.GetTempPath(),
                "codex-monitor-missing-" + Guid.NewGuid().ToString("N"));
            AppSettings settings = new AppSettings
            {
                CodexAccounts =
                [
                    CodexAccountSettings.Default,
                    new()
                    {
                        Id = "missing",
                        DisplayName = "Missing",
                        CodexHomePath = missingHome,
                    },
                ],
            }.Normalized();
            var window = new SettingsWindow(settings, () => settings);
            window.Loaded += (_, _) =>
            {
                window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(3, window.CategoryTabs.SelectedIndex);
                Assert.Equal(1, window.CodexAccountsGrid.SelectedIndex);
                Assert.Same(
                    window.CodexAccountsGrid.Columns[1],
                    window.CodexAccountsGrid.CurrentCell.Column);
                Assert.Contains("CODEX_HOME", window.ValidationMessage.Text, StringComparison.Ordinal);
                window.Close();
            };

            Assert.NotEqual(true, window.ShowDialog());
        });
    }

    [Fact]
    public void SettingsWindow_ShowsWaitingAfterSetupBeforeFirstObservation()
    {
        RunInSta(() =>
        {
            AppSettings settings = new AppSettings
            {
                ShowClaudeUsage = true,
                ClaudeSetupCompleted = true,
            }.Normalized();
            UsageSnapshot waiting = UsageSnapshot.Loading(
                UsageProvider.Claude,
                DateTimeOffset.UtcNow) with
            {
                Availability = UsageAvailability.Waiting,
            };
            var window = new SettingsWindow(
                settings,
                () => settings,
                () => waiting);
            try
            {
                Assert.Equal(
                    "接続済みです。最初の利用情報を待っています。",
                    window.ClaudeConnectionStatusText.Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void SettingsWindowSeparatesSelectedSourceAndChannelHealth()
    {
        RunInSta(() =>
        {
            AppSettings settings = new AppSettings
            {
                ShowClaudeUsage = true,
                ClaudeSetupCompleted = true,
            }.Normalized();
            DateTimeOffset activeAttempt =
                new(2026, 7, 27, 3, 10, 0, TimeSpan.Zero);
            DateTimeOffset passiveReceipt =
                new(2026, 7, 27, 3, 11, 0, TimeSpan.Zero);
            var window = new SettingsWindow(
                settings,
                () => settings,
                () => UsageSnapshot.Loading(UsageProvider.Claude, activeAttempt),
                () => "Claude Code statusLine（常駐セッション）",
                () => new(
                    ClaudeUsageSourceKind.StatusLinePassive,
                    activeAttempt,
                    "TIMEOUT",
                    passiveReceipt));
            try
            {
                string status = window.ClaudeConnectionStatusText.Text;
                Assert.Contains("現在表示元: Claude Code statusLine", status);
                Assert.Contains("active最終試行:", status);
                Assert.Contains("失敗: TIMEOUT", status);
                Assert.Contains("statusLine最終受信:", status);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("ScaleBox", "74", 0, "表示倍率")]
    [InlineData("ScaleBox", "201", 0, "表示倍率")]
    [InlineData("HorizontalMarginBox", "-1", 1, "水平余白")]
    [InlineData("VerticalMarginBox", "NaN", 1, "垂直余白")]
    [InlineData("RefreshBox", "59", 3, "更新間隔")]
    [InlineData("StartupTimeoutBox", "121", 3, "起動待ち")]
    public void SettingsWindow_RejectsInvalidValues(
        string fieldName,
        string value,
        int expectedTab,
        string expectedMessage)
    {
        RunInSta(() =>
        {
            var window = CreateWindow();
            Assert.IsType<TextBox>(window.FindName(fieldName)).Text = value;
            window.Loaded += (_, _) =>
            {
                window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(expectedTab, window.CategoryTabs.SelectedIndex);
                Assert.StartsWith(expectedMessage, window.ValidationMessage.Text, StringComparison.Ordinal);
                window.Close();
            };

            Assert.NotEqual(true, window.ShowDialog());
        });
    }

    [Fact]
    public void SettingsWindow_ClearsValidationAfterInputChanges()
    {
        RunInSta(() =>
        {
            var window = CreateWindow();
            window.DisplayModeBox.SelectedIndex = 1;
            window.ScaleBox.Text = "invalid";
            window.Loaded += (_, _) =>
            {
                window.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.NotEmpty(window.ValidationMessage.Text);
                Assert.Equal(1, window.DisplayModeBox.SelectedIndex);
                window.ScaleBox.Text = "100";
                Assert.Empty(window.ValidationMessage.Text);
                window.Close();
            };

            window.ShowDialog();
        });
    }

    private static SettingsWindow CreateWindow(AppSettings? settings = null)
    {
        AppSettings current = (settings ?? new AppSettings()).Normalized();
        return new SettingsWindow(current, () => current);
    }

    private static void Layout(Window window)
    {
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
    }

    private static void RunInSta(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception caught)
            {
                exception = caught;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (exception is not null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}
