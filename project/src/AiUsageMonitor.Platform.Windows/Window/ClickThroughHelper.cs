using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace AiUsageMonitor.Platform.Windows.Window;

[ExcludeFromCodeCoverage(
    Justification = "Thin user32 interop boundary verified by UI smoke testing.")]
public static partial class ClickThroughHelper
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;

    public static void Apply(nint windowHandle, bool enabled)
    {
        nint style = GetWindowLongPtr(windowHandle, GwlExStyle);
        long value = style.ToInt64() | WsExToolWindow;
        value = enabled ? value | WsExTransparent : value & ~WsExTransparent;
        SetWindowLongPtr(windowHandle, GwlExStyle, new nint(value));
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint windowHandle, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);
}
