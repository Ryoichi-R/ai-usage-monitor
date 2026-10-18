namespace AiUsageMonitor.App.UI.Hosting;

/// <summary>
/// 常駐アイコンのクリック通知からダブルクリックを判定する。AvaloniaのWindows実装は左ボタンを離すたび
/// （WM_LBUTTONUP）に<c>TrayIcon.Clicked</c>を出し、ダブルクリックを区別しないため、
/// OSのダブルクリック間隔内に続いた2回目のクリックをダブルクリックとして扱う。
/// </summary>
internal sealed class TrayDoubleClickDetector(Func<TimeSpan> doubleClickTime, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long? _previousClick;

    /// <summary>クリックを1回記録し、直前のクリックと合わせてダブルクリックになったときtrueを返す。</summary>
    public bool RegisterClick()
    {
        long now = _time.GetTimestamp();
        if (_previousClick is long previous && _time.GetElapsedTime(previous, now) <= doubleClickTime())
        {
            // 3回目のクリックは新しい1回目として数える。
            _previousClick = null;
            return true;
        }
        _previousClick = now;
        return false;
    }
}
