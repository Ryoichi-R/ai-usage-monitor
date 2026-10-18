using System.Windows;
using AiUsageMonitor.Core.Presentation;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.App.Tests;

[Trait("Category", "Interactive")]
public sealed class BackgroundLayerCoordinatorTests
{
    [Fact]
    public void InlineBackgroundAndVisibilityChangesDoNotOpenSeparateLayer()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var settings = new AppSettings
            {
                BackgroundEnabled = true,
                HideBackgroundBehindWindows = false,
                ClickThrough = false,
            };
            var main = new MainWindow
            {
                DataContext = MainWindowScaleTestSupport.CreateMaxDisplayViewModel(),
            };
            main.ApplySettings(settings, reposition: false);
            main.Show();
            using var coordinator = new BackgroundLayerCoordinator(main, main.Dispatcher);
            try
            {
                coordinator.Apply(settings);
                Assert.Equal(BackgroundPresentationMode.Inline, coordinator.Mode);
                Assert.False(coordinator.BackgroundWindow.IsVisible);

                coordinator.SetWidgetVisible(false);
                Assert.False(main.IsVisible);
                coordinator.SetWidgetVisible(true);
                Assert.True(main.IsVisible);
                coordinator.Reapply();
                coordinator.SyncNow();
                Assert.False(coordinator.BackgroundWindow.IsVisible);
            }
            finally
            {
                main.Close();
            }
        });
    }
    [Fact]
    public void EdgeFadeBackgroundExpandsBeyondInformationBounds()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var settings = new AppSettings
            {
                BackgroundEnabled = true,
                HideBackgroundBehindWindows = true,
                AlwaysOnTop = true,
                BackgroundFillMode = BackgroundFillMode.EdgeFade,
                BackgroundEdgeFadePercent = 25,
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
            try
            {
                coordinator.Apply(settings);
                coordinator.SyncNow();
                Rect information = main.GetInformationBoundsInScreenDip();
                Assert.Equal(information.Left - information.Width * .25, coordinator.BackgroundWindow.Left, 1);
                Assert.Equal(information.Top - information.Height * .25, coordinator.BackgroundWindow.Top, 1);
                Assert.Equal(information.Width * 1.5, coordinator.BackgroundWindow.Width, 1);
                Assert.Equal(information.Height * 1.5, coordinator.BackgroundWindow.Height, 1);
            }
            finally
            {
                main.Close();
            }
        });
    }

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
