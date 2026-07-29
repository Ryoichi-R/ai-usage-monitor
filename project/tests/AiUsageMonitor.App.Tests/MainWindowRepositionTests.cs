using System.Windows;
using System.Windows.Threading;
using AiUsageMonitor.Core.Settings;

namespace AiUsageMonitor.App.Tests;

// 配置予約ライフサイクルの回帰。ドラッグ開始前に積まれた予約がドラッグ後に位置を上書きしないこと、
// 終了処理中に予約が残らないことを、実表示(Show)して確認する。
[Trait("Category", "Interactive")]
public sealed class MainWindowRepositionTests
{
    [Fact]
    public void PendingReposition_IsAbortedWhenDragStarts()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = ShowWindow(out _);
            try
            {
                // ドラッグで確定させたい位置。
                const double draggedLeft = 321;
                const double draggedTop = 123;

                // ドラッグ開始「前」に配置予約を積む(サイズは変えず、予約だけを再現する)。
                // 実行されれば設定由来の四隅位置へ移動し、ドラッグ位置を上書きする。
                window.RequestRepositionForTest(fullApply: true);
                Assert.True(window.HasPendingPlacementForTest);

                // ユーザードラッグをシミュレート: 位置を確定し、開始時の破棄経路を呼ぶ。
                window.Left = draggedLeft;
                window.Top = draggedTop;
                // DPIスナップ後の実値を基準にする（上書きされないことの確認が目的）。
                double settledLeft = window.Left;
                double settledTop = window.Top;
                window.SimulateDragAbortPendingPlacement();
                Assert.False(window.HasPendingPlacementForTest);

                // 予約(Background)を消化しても、破棄済みなので位置は上書きされない。
                Drain(window);

                Assert.Equal(settledLeft, window.Left, 3);
                Assert.Equal(settledTop, window.Top, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Reposition_IsNotScheduledWhileClosing()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = ShowWindow(out _);
            window.Close();

            // 終了後の再配置要求は予約されず、保留中の予約も残っていない。
            window.ApplySettings(new AppSettings { UiScalePercent = 200 }, reposition: true);
            Assert.False(window.HasPendingPlacementForTest);
        });
    }

    [Fact]
    public void PendingReposition_IsAbortedByClosing()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = ShowWindow(out _);
            window.RequestRepositionForTest(fullApply: true);
            Assert.True(window.HasPendingPlacementForTest);
            Assert.True(window.PendingFullApplyForTest);

            window.Close();

            Assert.False(window.HasPendingPlacementForTest);
            Assert.False(window.PendingFullApplyForTest);
        });
    }

    [Fact]
    public void Reposition_IsNotScheduledBeforeWindowIsLoaded()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = new MainWindow();

            window.RequestRepositionForTest(fullApply: true);

            Assert.False(window.HasPendingPlacementForTest);
            Assert.False(window.PendingFullApplyForTest);
            window.Close();
        });
    }

    [Fact]
    public void Reposition_IsNotScheduledWhileDragging()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = ShowWindow(out _);
            try
            {
                window.DraggingForTest = true;

                window.RequestRepositionForTest(fullApply: true);

                Assert.False(window.HasPendingPlacementForTest);
                Assert.False(window.PendingFullApplyForTest);
            }
            finally
            {
                window.DraggingForTest = false;
                window.Close();
            }
        });
    }

    [Fact]
    public void SizeChangedCoalesces_IntoSinglePendingOperation()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = ShowWindow(out _);
            try
            {
                Drain(window);
                // 連続でreposition要求しても、保留予約は高々1件に束ねられる。
                window.RequestRepositionForTest(fullApply: false);
                window.RequestRepositionForTest(fullApply: false);
                window.RequestRepositionForTest(fullApply: true);
                Assert.True(window.HasPendingPlacementForTest);
                Drain(window);
                Assert.False(window.HasPendingPlacementForTest);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Abort_ClearsPendingFullApplyFlag()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = ShowWindow(out _);
            try
            {
                window.RequestRepositionForTest(fullApply: true);
                Assert.True(window.HasPendingPlacementForTest);
                Assert.True(window.PendingFullApplyForTest);

                window.SimulateDragAbortPendingPlacement();

                // 破棄時にfullフラグも落ちること。残ると次のclamp要求が完全再配置へ化ける。
                Assert.False(window.HasPendingPlacementForTest);
                Assert.False(window.PendingFullApplyForTest);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void DragEnd_SwitchesModeToCustom_AndNextSizeChangedKeepsPosition()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            // 既定Presetから開始し、ドラッグ完了で自由配置へ切り替わることを確認する。
            var window = ShowWindow(out _);
            try
            {
                Assert.Equal(PlacementMode.Preset, window.Settings.PlacementMode);

                // ドラッグをシミュレート: 位置を確定し、本番のドラッグ完了経路を通す。
                window.DraggingForTest = true;
                window.Left = 300;
                window.Top = 200;
                double settledLeft = window.Left;
                double settledTop = window.Top;
                window.SimulateDragAbortPendingPlacement();
                window.CompleteDragForTest();

                Assert.Equal(PlacementMode.Custom, window.Settings.PlacementMode);

                // 次のSizeChanged相当(clampのみ)で、四隅へ戻らずドラッグ位置を保持する。
                window.RequestRepositionForTest(fullApply: false);
                Drain(window);

                Assert.Equal(settledLeft, window.Left, 3);
                Assert.Equal(settledTop, window.Top, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PresetMode_NextSizeChangedReappliesAnchorPosition()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = ShowWindow(out _);
            try
            {
                Assert.Equal(PlacementMode.Preset, window.Settings.PlacementMode);
                double anchoredLeft = window.Left;
                double anchoredTop = window.Top;
                window.Left = 300;
                window.Top = 200;

                window.RequestRepositionForTest(fullApply: false);
                Drain(window);

                Assert.Equal(anchoredLeft, window.Left, 3);
                Assert.Equal(anchoredTop, window.Top, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PendingCallback_WhileDragging_DoesNotReposition()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            // Abortで止められない「実行開始済み」経路: コールバック冒頭の再判定で弾かれること。
            var window = ShowWindow(out _);
            try
            {
                window.Left = 260;
                window.Top = 140;
                double settledLeft = window.Left;
                double settledTop = window.Top;

                window.RequestRepositionForTest(fullApply: true);
                Assert.True(window.HasPendingPlacementForTest);

                // ドラッグ中にコールバックが実行開始した状況を再現する。
                window.DraggingForTest = true;
                Drain(window);
                window.DraggingForTest = false;

                Assert.False(window.HasPendingPlacementForTest);
                Assert.Equal(settledLeft, window.Left, 3);
                Assert.Equal(settledTop, window.Top, 3);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Reposition_IsNotScheduledWhileHidden_AndRunsOnReshow()
    {
        MainWindowScaleTestSupport.RunInSta(() =>
        {
            var window = ShowWindow(out _);
            try
            {
                window.Hide();
                Drain(window);

                // 非表示中は予約されない。
                window.RequestRepositionForTest(fullApply: true);
                Assert.False(window.HasPendingPlacementForTest);

                // 再表示でIsVisibleChanged→trueにより1回予約される。
                window.Show();
                Assert.True(window.HasPendingPlacementForTest);
                Drain(window);
                Assert.False(window.HasPendingPlacementForTest);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MainWindow ShowWindow(out MainWindow created)
    {
        var window = new MainWindow { DataContext = MainWindowScaleTestSupport.CreateMaxDisplayViewModel() };
        window.ApplySettings(new AppSettings { UiScalePercent = 100, ClickThrough = false }, reposition: false);
        window.Show();
        Drain(window);
        created = window;
        return window;
    }

    private static void Drain(Window window) =>
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
}
