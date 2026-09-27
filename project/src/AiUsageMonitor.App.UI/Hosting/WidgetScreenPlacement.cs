using AiUsageMonitor.Core.Settings;
using Avalonia;

namespace AiUsageMonitor.App.UI.Hosting;

/// <summary>
/// 配置計算に使うディスプレイ情報。<see cref="WorkingArea"/>はAvaloniaのscreen座標（Windowsは物理pixel、
/// macOSはpoint）、<see cref="Scaling"/>はDIPからscreen座標への倍率（macOSのAvaloniaは常に1）。
/// </summary>
public readonly record struct ScreenInfo(
    string DeviceName,
    string? StableId,
    bool IsPrimary,
    PixelRect WorkingArea,
    double Scaling)
{
    public double SafeScaling => double.IsFinite(Scaling) && Scaling > 0 ? Scaling : 1d;

    /// <summary>作業領域をDIP単位で返す。</summary>
    public WorkArea DipWorkArea => new(
        WorkingArea.X / SafeScaling,
        WorkingArea.Y / SafeScaling,
        WorkingArea.Width / SafeScaling,
        WorkingArea.Height / SafeScaling);
}

/// <summary>
/// ウィンドウ外形に対する情報領域（背景fadeを除く表示部分）の大きさと内側オフセット（DIP）。
/// 配置・位置保存は情報領域を基準にする（WPF版と同じ）。
/// </summary>
public readonly record struct InformationGeometry(double WidthDip, double HeightDip, double InsetXDip, double InsetYDip);

/// <summary>
/// ディスプレイ選択・配置・位置保存の純粋関数群。Windows版<c>DisplayWorkAreaProvider</c>の照合規則と
/// <see cref="WidgetPlacementCalculator"/>を、Avaloniaのscreen座標へ橋渡しする。
/// </summary>
public static class WidgetScreenPlacement
{
    /// <summary>
    /// 保存済みの表示先を解決する。固有IDが保存されていれば固有IDだけで照合し、なければdevice名で照合する
    /// （<see cref="Settings.MonitorChoice.Select"/>と同じ規則）。見つからなければプライマリ、次に先頭を返す。
    /// </summary>
    public static ScreenInfo? SelectScreen(IReadOnlyList<ScreenInfo> screens, string? savedDeviceName, string? savedStableId)
    {
        ArgumentNullException.ThrowIfNull(screens);
        if (screens.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(savedStableId))
        {
            foreach (ScreenInfo screen in screens)
            {
                if (string.Equals(screen.StableId, savedStableId, StringComparison.OrdinalIgnoreCase)) return screen;
            }
        }
        else if (!string.IsNullOrWhiteSpace(savedDeviceName))
        {
            foreach (ScreenInfo screen in screens)
            {
                if (string.Equals(screen.DeviceName, savedDeviceName, StringComparison.OrdinalIgnoreCase)) return screen;
            }
        }
        foreach (ScreenInfo screen in screens)
        {
            if (screen.IsPrimary) return screen;
        }
        return screens[0];
    }

    /// <summary>screen座標の点を含むディスプレイ。どれにも含まれなければ最も近いものを返す。</summary>
    public static ScreenInfo? ScreenContaining(IReadOnlyList<ScreenInfo> screens, PixelPoint point)
    {
        ArgumentNullException.ThrowIfNull(screens);
        ScreenInfo? nearest = null;
        double nearestDistance = double.MaxValue;
        foreach (ScreenInfo screen in screens)
        {
            PixelRect area = screen.WorkingArea;
            if (area.Contains(point)) return screen;
            double dx = Math.Max(0, Math.Max(area.X - point.X, point.X - area.Right));
            double dy = Math.Max(0, Math.Max(area.Y - point.Y, point.Y - area.Bottom));
            double distance = (dx * dx) + (dy * dy);
            if (distance < nearestDistance)
            {
                nearest = screen;
                nearestDistance = distance;
            }
        }
        return nearest;
    }

    /// <summary>設定（プリセットまたは自由配置のfraction）からウィンドウ外形の左上位置を求める。</summary>
    public static PixelPoint Calculate(ScreenInfo screen, InformationGeometry information, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WidgetPlacement placement = WidgetPlacementCalculator.Calculate(
            screen.DipWorkArea,
            information.WidthDip,
            information.HeightDip,
            settings);
        return ToWindowPosition(screen, information, placement);
    }

    /// <summary>現在位置を作業領域内へclampする。fractionは再計算しない（大きさ変化時の自由配置向け）。</summary>
    public static PixelPoint ClampCurrent(ScreenInfo screen, InformationGeometry information, PixelPoint windowPosition)
    {
        (double left, double top) = InformationTopLeftDip(screen, information, windowPosition);
        WidgetPlacement placement = WidgetPlacementCalculator.ClampToArea(
            screen.DipWorkArea,
            information.WidthDip,
            information.HeightDip,
            left,
            top);
        return ToWindowPosition(screen, information, placement);
    }

    /// <summary>現在位置を自由配置の設定（表示先とfraction）として取り込む。</summary>
    public static AppSettings Capture(
        AppSettings settings,
        ScreenInfo screen,
        InformationGeometry information,
        PixelPoint windowPosition)
    {
        ArgumentNullException.ThrowIfNull(settings);
        WorkArea area = screen.DipWorkArea;
        (double left, double top) = InformationTopLeftDip(screen, information, windowPosition);
        double availableWidth = Math.Max(0, area.Width - Math.Max(0, information.WidthDip));
        double availableHeight = Math.Max(0, area.Height - Math.Max(0, information.HeightDip));
        double leftFraction = availableWidth <= 0 ? 0 : (left - area.Left) / availableWidth;
        double topFraction = availableHeight <= 0 ? 0 : (top - area.Top) / availableHeight;
        if (!double.IsFinite(leftFraction) || !double.IsFinite(topFraction)) return settings;
        return settings with
        {
            MonitorDeviceName = screen.DeviceName,
            MonitorStableId = screen.StableId,
            PlacementMode = PlacementMode.Custom,
            CustomLeftFraction = Math.Clamp(leftFraction, 0, 1),
            CustomTopFraction = Math.Clamp(topFraction, 0, 1),
        };
    }

    private static (double Left, double Top) InformationTopLeftDip(
        ScreenInfo screen,
        InformationGeometry information,
        PixelPoint windowPosition) =>
        (
            (windowPosition.X / screen.SafeScaling) + information.InsetXDip,
            (windowPosition.Y / screen.SafeScaling) + information.InsetYDip);

    private static PixelPoint ToWindowPosition(ScreenInfo screen, InformationGeometry information, WidgetPlacement placement) =>
        new(
            (int)Math.Round((placement.Left - information.InsetXDip) * screen.SafeScaling),
            (int)Math.Round((placement.Top - information.InsetYDip) * screen.SafeScaling));
}
