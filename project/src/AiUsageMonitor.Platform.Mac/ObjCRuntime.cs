using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>
/// NSWindowへ直接メッセージを送る最小のobjc interop。P0-2のウィジェットPoC（2026-09-26）で
/// 挙動を確認した呼出しだけを持つ。
/// </summary>
/// <summary>NSWindowCollectionBehaviorの値（AppKitの定義どおり）。</summary>
internal static class NSWindowCollectionBehavior
{
    internal const ulong CanJoinAllSpaces = 1 << 0;
    internal const ulong Stationary = 1 << 4;
    internal const ulong IgnoresCycle = 1 << 6;
    internal const ulong FullScreenAuxiliary = 1 << 8;
}

[SupportedOSPlatform("macos")]
internal static partial class ObjCRuntime
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphicsLibrary = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // CGWindowLevelKey
    internal const int DesktopWindowLevelKey = 2;
    internal const int NormalWindowLevelKey = 4;
    internal const int FloatingWindowLevelKey = 5;

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendPtr(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint SendPtrPtr(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial long SendLong(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial ulong SendULong(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial byte SendByte(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidLong(nint receiver, nint selector, long value);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidULong(nint receiver, nint selector, ulong value);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendVoidByte(nint receiver, nint selector, byte value);

    [LibraryImport(CoreGraphicsLibrary)]
    internal static partial int CGWindowLevelForKey(int key);

    private static nint Sel(string name) => sel_registerName(name);

    /// <summary>NSWindowならそのまま、NSViewならその所属ウィンドウを返す。どちらでもなければ0。</summary>
    internal static nint ResolveWindow(nint handle)
    {
        if (handle == 0) return 0;
        if (IsKindOf(handle, "NSWindow")) return handle;
        return IsKindOf(handle, "NSView") ? SendPtr(handle, Sel("window")) : 0;
    }

    internal static long Level(nint window) => SendLong(window, Sel("level"));

    internal static void SetLevel(nint window, long level) => SendVoidLong(window, Sel("setLevel:"), level);

    internal static bool IgnoresMouseEvents(nint window) => SendByte(window, Sel("ignoresMouseEvents")) != 0;

    internal static void SetIgnoresMouseEvents(nint window, bool value) =>
        SendVoidByte(window, Sel("setIgnoresMouseEvents:"), value ? (byte)1 : (byte)0);

    internal static ulong CollectionBehavior(nint window) => SendULong(window, Sel("collectionBehavior"));

    internal static void SetCollectionBehavior(nint window, ulong value) =>
        SendVoidULong(window, Sel("setCollectionBehavior:"), value);

    private static bool IsKindOf(nint instance, string className)
    {
        nint cls = objc_getClass(className);
        return cls != 0 && SendPtrPtr(instance, Sel("isKindOfClass:"), cls) != 0;
    }
}
