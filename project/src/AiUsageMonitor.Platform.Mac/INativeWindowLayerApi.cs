using System.Runtime.Versioning;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>NSWindowの層・マウス透過・Space挙動の読み書き。テストではfakeへ差し替える。</summary>
internal interface INativeWindowLayerApi
{
    nint ResolveWindow(nint handle);
    long LevelFor(WidgetLayerMode mode);
    long GetLevel(nint window);
    void SetLevel(nint window, long level);
    bool GetIgnoresMouseEvents(nint window);
    void SetIgnoresMouseEvents(nint window, bool value);
    ulong GetCollectionBehavior(nint window);
    void SetCollectionBehavior(nint window, ulong value);
}

[SupportedOSPlatform("macos")]
internal sealed class ObjCWindowLayerApi : INativeWindowLayerApi
{
    public static ObjCWindowLayerApi Instance { get; } = new();

    public nint ResolveWindow(nint handle) => ObjCRuntime.ResolveWindow(handle);

    public long LevelFor(WidgetLayerMode mode) => ObjCRuntime.CGWindowLevelForKey(mode switch
    {
        WidgetLayerMode.AlwaysOnTop => ObjCRuntime.FloatingWindowLevelKey,
        WidgetLayerMode.AlwaysOnBottom => ObjCRuntime.DesktopWindowLevelKey,
        _ => ObjCRuntime.NormalWindowLevelKey,
    });

    public long GetLevel(nint window) => ObjCRuntime.Level(window);

    public void SetLevel(nint window, long level) => ObjCRuntime.SetLevel(window, level);

    public bool GetIgnoresMouseEvents(nint window) => ObjCRuntime.IgnoresMouseEvents(window);

    public void SetIgnoresMouseEvents(nint window, bool value) => ObjCRuntime.SetIgnoresMouseEvents(window, value);

    public ulong GetCollectionBehavior(nint window) => ObjCRuntime.CollectionBehavior(window);

    public void SetCollectionBehavior(nint window, ulong value) => ObjCRuntime.SetCollectionBehavior(window, value);
}
