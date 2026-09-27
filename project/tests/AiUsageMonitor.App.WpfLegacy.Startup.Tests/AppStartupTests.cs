using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using System.Windows;
using System.Windows.Documents;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows.Threading;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Claude.Acquisition;
using AiUsageMonitor.Platform.Windows;

namespace AiUsageMonitor.App.Tests;

[Trait("Category", "Interactive")]
public sealed class AppStartupTests
{
    [Fact]
    public void StartupWithDisabledProvidersUsesIsolatedSettingsAndClosesCleanly()
    {
        string localData = Path.Combine(
            Path.GetTempPath(),
            "AiUsageMonitorAppStartup-" + Guid.NewGuid().ToString("N"));
        var paths = new WindowsAppPathProvider(localData);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.SettingsFilePath)!);
        var claude = new FakeClaudeSource();
        File.WriteAllText(paths.SettingsFilePath, JsonSerializer.Serialize(new AppSettings
        {
            ShowCodexUsage = false,
            ShowAdditionalUsage = false,
            ShowCredits = false,
            ShowClaudeUsage = true,
            CodexMonetarySettingsInitialized = true,
        }));

        Exception? failure = null;
        bool completed = false;
        bool timedOut = false;
        bool startupEvent = false;
        int? exitCode = null;
        var thread = new Thread(() =>
        {
            string name = Guid.NewGuid().ToString("N");
            var app = new App(paths, "Local\\AiUsageMonitor-Test-Mutex-" + name,
                "Local\\AiUsageMonitor-Test-Activate-" + name,
                () => new TrayController(new Icon(SystemIcons.Application, 16, 16)),
                _ => { }, _ => claude, startPassiveListener: false);
            app.InitializeComponent();
            app.Startup += (_, _) => startupEvent = true;
            app.Exit += (_, e) => exitCode = e.ApplicationExitCode;
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            timeout.Tick += (_, _) =>
            {
                timedOut = true;
                timeout.Stop();
                app.Shutdown();
            };
            app.StartupFailed += exception => failure = exception;
            app.StartupCompleted += async () =>
            {
                try
                {
                    MainWindow window = Assert.Single(app.Windows.OfType<MainWindow>());
                    Assert.True(window.IsVisible);
                    Assert.False(window.Settings.ShowCodexUsage);
                    Assert.True(window.Settings.ShowClaudeUsage);
                    Assert.True(claude.CallCount >= 1);

                    var tray = Assert.IsType<TrayController>(typeof(App)
                        .GetField("_tray", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(app));
                    var icon = Assert.IsType<NotifyIcon>(typeof(TrayController)
                        .GetField("_icon", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(tray));
                    var menu = Assert.IsType<ContextMenuStrip>(icon.ContextMenuStrip);
                    var display = Assert.IsType<ToolStripMenuItem>(menu.Items[5]);
                    display.DropDownItems[1].PerformClick();
                    for (int attempt = 0; attempt < 100 &&
                        window.Settings.DisplayMode != WidgetDisplayMode.Compact; attempt++)
                    {
                        await Task.Delay(10);
                    }
                    Assert.Equal(WidgetDisplayMode.Compact, window.Settings.DisplayMode);
                    menu.Items[4].PerformClick();
                    Assert.False(window.IsVisible);
                    menu.Items[4].PerformClick();
                    Assert.True(window.IsVisible);
                    int beforeRefresh = claude.CallCount;
                    menu.Items[3].PerformClick();
                    for (int attempt = 0; attempt < 100 && claude.CallCount == beforeRefresh; attempt++)
                    {
                        await Task.Delay(10);
                    }
                    Assert.True(claude.CallCount > beforeRefresh);

                    Exception? dialogFailure = null;
                    _ = app.Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            SettingsWindow dialog = Assert.Single(app.Windows.OfType<SettingsWindow>());
                            dialog.ScaleBox.Text = "125";
                            dialog.ShowClaudeBox.IsChecked = false;
                            dialog.SaveButton.RaiseEvent(new RoutedEventArgs(
                                System.Windows.Controls.Button.ClickEvent));
                        }
                        catch (Exception exception)
                        {
                            dialogFailure = exception;
                            app.Windows.OfType<SettingsWindow>().FirstOrDefault()?.Close();
                        }
                    });
                    menu.Items[2].PerformClick();
                    if (dialogFailure is not null) ExceptionDispatchInfo.Capture(dialogFailure).Throw();
                    for (int attempt = 0; attempt < 100 && window.Settings.UiScalePercent != 125; attempt++)
                    {
                        await Task.Delay(10);
                    }
                    Assert.Equal(125, window.Settings.UiScalePercent);
                    AppSettings saved = JsonSerializer.Deserialize<AppSettings>(
                        File.ReadAllText(paths.SettingsFilePath))!;
                    Assert.Equal(125, saved.UiScalePercent);
                    Assert.False(saved.ShowClaudeUsage);

                    var welcomeTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(20),
                    };
                    welcomeTimer.Tick += (_, _) =>
                    {
                        WelcomeWindow? welcome = app.Windows.OfType<WelcomeWindow>().FirstOrDefault();
                        if (welcome is null) return;
                        welcomeTimer.Stop();
                        welcome.CodexOnlyButton.RaiseEvent(new RoutedEventArgs(
                            System.Windows.Controls.Button.ClickEvent));
                    };
                    welcomeTimer.Start();
                    var firstRun = Assert.IsAssignableFrom<Task>(typeof(App)
                        .GetMethod("ShowFirstRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(app, null));
                    await firstRun;
                    welcomeTimer.Stop();
                    Assert.False(window.Settings.ShowClaudeUsage);

                    bool setupOpened = false;
                    Exception? setupFailure = null;
                    var setupTimer = new DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(20),
                    };
                    setupTimer.Tick += (_, _) =>
                    {
                        ClaudeSetupWindow? dialog = app.Windows.OfType<ClaudeSetupWindow>()
                            .FirstOrDefault();
                        if (dialog is null) return;
                        setupTimer.Stop();
                        try
                        {
                            Assert.StartsWith(localData, dialog.SettingsFolderBox.Text,
                                StringComparison.OrdinalIgnoreCase);
                            dialog.TestConnectionButton.RaiseEvent(new RoutedEventArgs(
                                System.Windows.Controls.Button.ClickEvent));
                            Assert.Contains("取得できました", dialog.ActionMessageText.Text,
                                StringComparison.Ordinal);

                            var readmeTimer = new DispatcherTimer
                            {
                                Interval = TimeSpan.FromMilliseconds(20),
                            };
                            bool readmeOpened = false;
                            readmeTimer.Tick += (_, _) =>
                            {
                                ReadmeSectionWindow? readme = app.Windows
                                    .OfType<ReadmeSectionWindow>().FirstOrDefault();
                                if (readme is null) return;
                                readmeTimer.Stop();
                                readmeOpened = readme.ReadmeSectionBox.Text.Contains(
                                    "LLMによるセットアップ支援", StringComparison.Ordinal);
                                readme.Close();
                            };
                            readmeTimer.Start();
                            dialog.LlmSetupSupportLink.RaiseEvent(new RoutedEventArgs(
                                Hyperlink.ClickEvent));
                            readmeTimer.Stop();
                            Assert.True(readmeOpened);
                            Assert.Contains("README", dialog.ActionMessageText.Text,
                                StringComparison.Ordinal);
                            setupOpened = true;
                        }
                        catch (Exception exception)
                        {
                            setupFailure = exception;
                        }
                        finally
                        {
                            dialog.Close();
                        }
                    };
                    setupTimer.Start();
                    menu.Items[0].PerformClick();
                    for (int attempt = 0; attempt < 100 && !setupOpened && setupFailure is null; attempt++)
                    {
                        await Task.Delay(10);
                    }
                    setupTimer.Stop();
                    if (setupFailure is not null) ExceptionDispatchInfo.Capture(setupFailure).Throw();
                    Assert.True(setupOpened);
                    for (int attempt = 0; attempt < 100; attempt++)
                    {
                        saved = JsonSerializer.Deserialize<AppSettings>(
                            File.ReadAllText(paths.SettingsFilePath))!;
                        if (saved.ClaudeSetupCompleted) break;
                        await Task.Delay(10);
                    }
                    Assert.True(saved.ShowClaudeUsage);
                    Assert.True(saved.ClaudeSetupCompleted);
                    completed = true;
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    timeout.Stop();
                    _ = app.Dispatcher.BeginInvoke(app.Shutdown);
                }
            };
            timeout.Start();
            try
            {
                app.Run();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                timeout.Stop();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Isolated application did not exit.");
        try
        {
            Assert.False(timedOut, "Isolated application did not complete startup.");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.True(completed, $"Startup event: {startupEvent}; exit code: {exitCode}");
        }
        finally
        {
            if (Directory.Exists(localData)) Directory.Delete(localData, recursive: true);
        }
    }

    private sealed class FakeClaudeSource : IClaudeUsageSource
    {
        public int CallCount { get; private set; }

        public Task<ClaudeUsageObservation> RefreshAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            UsageSnapshot snapshot = UsageSnapshot.Loading(UsageProvider.Claude, now) with
            {
                Availability = UsageAvailability.Available,
                LastSuccessfulAt = now,
            };
            return Task.FromResult(new ClaudeUsageObservation(
                ClaudeUsageSourceKind.CliScreen, snapshot));
        }
    }
}