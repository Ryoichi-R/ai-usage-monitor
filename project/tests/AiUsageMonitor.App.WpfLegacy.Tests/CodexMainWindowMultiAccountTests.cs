using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AiUsageMonitor.Core.Settings;
using AiUsageMonitor.Core.Usage;
using AiUsageMonitor.Codex.Runtime;

namespace AiUsageMonitor.App.Tests;

[Trait("Category", "Interactive")]
public sealed class CodexMainWindowMultiAccountTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 14, 30, 0, TimeSpan.FromHours(9));
    private static readonly TimeZoneInfo Tokyo =
        TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    [Fact]
    public void ShownOverflowGuidesClickThroughAndChildMouseEventReachesRoot()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var viewModel = new UsageViewModel(() => Now, Tokyo);
            CodexAccountSettings[] accounts = Enumerable.Range(0, 30)
                .Select(index => new CodexAccountSettings
                {
                    Id = $"account-{index:D2}",
                    DisplayName = $"CODEX account with a long display name {index:D2}",
                    CodexHomePath = $@"C:\codex-{index:D2}",
                })
                .ToArray();
            viewModel.SynchronizeCodexAccounts(accounts, true, true, true);
            foreach (CodexAccountSettings account in accounts)
            {
                viewModel.Apply(new CodexAccountSnapshot(
                    account.Id,
                    account.DisplayName,
                    true,
                    Snapshot()));
            }
            Assert.All(
                viewModel.CodexAccounts,
                account => Assert.Equal(
                    "取得 14:25",
                    account.Usage.FreshnessText));
            var window = new MainWindow { DataContext = viewModel };
            var settings = new AppSettings
            {
                UiScalePercent = 200,
                ClickThrough = true,
                CodexAccounts = accounts,
            }.Normalized();

            try
            {
                window.ApplySettings(settings, false);
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.RepositionAfterContentChange();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var scroll = Assert.IsType<ScrollViewer>(
                    window.FindName("ContentScrollViewer"));
                var guidance = Assert.IsType<TextBlock>(
                    window.FindName("ScrollGuidanceText"));
                Assert.True(scroll.ExtentHeight > scroll.ViewportHeight);
                Assert.Equal(Visibility.Visible, guidance.Visibility);

                window.ApplySettings(settings with { ClickThrough = false }, false);
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Collapsed, guidance.Visibility);

                TextBlock child = Assert.IsType<TextBlock>(
                    FindVisualDescendant<TextBlock>(scroll));
                int before = window.RootMouseDownCountForTest;
                var mouseDown = new MouseButtonEventArgs(
                    Mouse.PrimaryDevice,
                    Environment.TickCount,
                    MouseButton.Left)
                {
                    RoutedEvent = Mouse.MouseDownEvent,
                    Source = child,
                };
                child.RaiseEvent(mouseDown);

                Assert.Equal(before + 1, window.RootMouseDownCountForTest);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static UsageSnapshot Snapshot() =>
        new(
            UsageProvider.Codex,
            Now.AddMinutes(-5),
            Now.AddMinutes(-5),
            UsageAvailability.Available,
            null,
            null,
            [
                new(
                    null,
                    null,
                    "five_hour",
                    25,
                    UsageWindowPolicy.FiveHourDurationMinutes,
                    Now.AddHours(1),
                    null),
            ],
            null,
            false,
            Now.AddMinutes(-5));

    private static T? FindVisualDescendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            T? descendant = FindVisualDescendant<T>(child);
            if (descendant is not null)
                return descendant;
        }
        return null;
    }
}
