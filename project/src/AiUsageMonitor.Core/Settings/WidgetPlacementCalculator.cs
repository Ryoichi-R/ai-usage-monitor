namespace AiUsageMonitor.Core.Settings;

/// <summary>作業領域（WorkingArea）をDIP単位で表す。左上原点。</summary>
public readonly record struct WorkArea(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}

/// <summary>ウィジェットの配置結果（Left / Top）をDIP単位で表す。</summary>
public readonly record struct WidgetPlacement(double Left, double Top);

/// <summary>
/// メインウィジェットの配置座標を、作業領域・ウィジェット外形寸法・設定から算出する純粋関数群。
/// WPF / WinFormsの型に依存しないため、実表示なしで網羅テストできる。
/// 作業領域より大きいウィジェットは、指定余白より作業領域内への収まりを優先してclampする。
/// </summary>
public static class WidgetPlacementCalculator
{
    /// <summary>プリセット配置または自由配置のfractionから配置を算出し、作業領域内へclampする。</summary>
    public static WidgetPlacement Calculate(WorkArea area, double widgetWidth, double widgetHeight, AppSettings settings)
    {
        double left;
        double top;
        if (settings.PlacementMode == PlacementMode.Custom &&
            settings.CustomLeftFraction is { } leftFraction &&
            settings.CustomTopFraction is { } topFraction)
        {
            left = area.Left + (Math.Max(0, area.Width - widgetWidth) * leftFraction);
            top = area.Top + (Math.Max(0, area.Height - widgetHeight) * topFraction);
        }
        else
        {
            bool right = settings.Anchor is PlacementAnchor.TopRight or PlacementAnchor.BottomRight;
            bool bottom = settings.Anchor is PlacementAnchor.BottomLeft or PlacementAnchor.BottomRight;
            left = right ? area.Right - widgetWidth - settings.HorizontalMarginDip : area.Left + settings.HorizontalMarginDip;
            top = bottom ? area.Bottom - widgetHeight - settings.VerticalMarginDip : area.Top + settings.VerticalMarginDip;
        }

        return ClampToArea(area, widgetWidth, widgetHeight, left, top);
    }

    /// <summary>現在位置を作業領域内へclampする。fractionの再計算は行わない（SizeChanged由来の自由配置向け）。</summary>
    public static WidgetPlacement ClampToArea(WorkArea area, double widgetWidth, double widgetHeight, double left, double top)
        => new(Clamp(left, area.Left, area.Right - widgetWidth), Clamp(top, area.Top, area.Bottom - widgetHeight));

    // ウィジェットが作業領域より大きい場合、maxが下限を下回るため左辺・上辺（min）を優先する。
    // これにより読みたい見出し・数値が作業領域内に残り、はみ出しは右辺・下辺側へ寄る。
    private static double Clamp(double value, double min, double maxCandidate)
    {
        double max = Math.Max(min, maxCandidate);
        return Math.Clamp(value, min, max);
    }
}
