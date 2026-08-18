using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.App.Tests;

[Trait("Category", "Interactive")]
public sealed class BackgroundLayerCoordinatorTests
{
    [Fact]
    public void SplitBackgroundWaitsForPlacementAndFallbackCanRevealIt()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var settings = new AppSettings
            {
                BackgroundEnabled = true,
                HideBackgroundBehindWindows = true,
                AlwaysOnTop = true,
                ClickThrough = false,
            };
            var main = new MainWindow
            {
                DataContext = MainWindowScaleTestSupport.CreateMaxDisplayViewModel(),
            };
            main.ApplySettings(settings, reposition: false);
            main.Show();
            main.DrainPendingPlacementForTest();

            using var coordinator = new BackgroundLayerCoordinator(main, main.Dispatcher);
            coordinator.Apply(settings);

            Assert.Equal(BackgroundPresentationMode.Split, coordinator.Mode);
            Assert.True(coordinator.AwaitingPlacementForTest);
            Assert.False(coordinator.BackgroundWindow.IsVisible);

            main.RequestRepositionForTest(fullApply: true);
            main.DrainPendingPlacementForTest();
            Assert.False(coordinator.AwaitingPlacementForTest);
            Assert.True(coordinator.BackgroundWindow.IsVisible);

            main.Hide();
            main.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.False(coordinator.BackgroundWindow.IsVisible);

            main.Show();
            main.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            coordinator.Reapply();
            Assert.True(coordinator.AwaitingPlacementForTest);
            coordinator.TriggerFallbackForTest();
            Assert.True(coordinator.BackgroundWindow.IsVisible);

            main.Close();
        });
    }
}
