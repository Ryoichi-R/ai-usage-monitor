namespace AiUsageMonitor.Platform;

/// <summary>ウィジェットウィンドウのZオーダー制御モード。</summary>
public enum WidgetLayerMode
{
    Normal,
    AlwaysOnTop,
    AlwaysOnBottom,
}

public readonly record struct WidgetLayerHealth(bool IsDegraded, string? Operation, int ErrorCode);

/// <summary>
/// 常に手前 / デスクトップ最背面 / クリック透過を統合するウィンドウ層制御の抽象。
/// Windows実装はSetWindowPos + WS_EX_TRANSPARENT + SetWinEventHookで自己修復する。
/// macOS実装はNSWindow.level / ignoresMouseEvents / collectionBehaviorで実現し、
/// OS側が位置を維持するため自己修復イベントは発火しない見込みである。
/// </summary>
public interface IWidgetLayerController : IDisposable
{
    WidgetLayerHealth Health { get; }

    /// <summary>プラットフォームのネイティブウィンドウハンドル（Windows: HWND、macOS: NSWindowポインタ）に紐づける。</summary>
    void Attach(nint nativeWindowHandle);

    void SetLayerMode(WidgetLayerMode mode);

    void SetClickThrough(bool enabled);

    /// <summary>他のtopmostウィンドウとの競合等から層順序を回復させる。自己修復が不要な実装ではno-opでよい。</summary>
    bool TryRecoverLayer();

    event Action<WidgetLayerHealth>? HealthChanged;
}
