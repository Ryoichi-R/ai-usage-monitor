using System.Runtime.InteropServices;

namespace AiUsageMonitor.Platform.Mac;

/// <summary>
/// ディスプレイの固有ID。CGDirectDisplayIDは接続・再起動で振り直されるため、
/// ColorSyncが返すディスプレイUUIDを表示先の保存に使う（Windowsのdevice interface pathに相当）。
/// </summary>
public static partial class MacDisplayIdentity
{
    public const string PlatformHandleDescriptor = "CGDirectDisplayID";

    private const string ColorSync = "/System/Library/Frameworks/ColorSync.framework/ColorSync";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint Utf8Encoding = 0x08000100;

    /// <summary>Avaloniaの<c>Screen.TryGetPlatformHandle()</c>の値からUUID文字列を得る。得られなければnull。</summary>
    public static string? TryGetStableId(nint handle, string? descriptor)
    {
        if (!OperatingSystem.IsMacOS() || handle == 0 ||
            !string.Equals(descriptor, PlatformHandleDescriptor, StringComparison.Ordinal))
        {
            return null;
        }
        nint uuid = CGDisplayCreateUUIDFromDisplayID((uint)handle);
        if (uuid == 0) return null;
        try
        {
            nint text = CFUUIDCreateString(0, uuid);
            if (text == 0) return null;
            try
            {
                return ReadCFString(text);
            }
            finally
            {
                CFRelease(text);
            }
        }
        finally
        {
            CFRelease(uuid);
        }
    }

    private static unsafe string? ReadCFString(nint text)
    {
        const int capacity = 64;
        byte* buffer = stackalloc byte[capacity];
        return CFStringGetCString(text, buffer, capacity, Utf8Encoding)
            ? Marshal.PtrToStringUTF8((nint)buffer)
            : null;
    }

    [LibraryImport(ColorSync)]
    private static partial nint CGDisplayCreateUUIDFromDisplayID(uint display);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFUUIDCreateString(nint allocator, nint uuid);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static unsafe partial bool CFStringGetCString(nint text, byte* buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
