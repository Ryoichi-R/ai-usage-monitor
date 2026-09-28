using System.ComponentModel;
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
    private const long WsExLayered = 0x80000;

    public static void Apply(nint windowHandle, bool enabled)
    {
        nint style = GetWindowLongPtr(windowHandle, GwlExStyle);
        ThrowOnFailure(style);
        long value = style.ToInt64() | WsExToolWindow;
        // Avalonia's composition transparency does not imply WS_EX_LAYERED.
        // Cross-process mouse passthrough requires both LAYERED and TRANSPARENT.
        bool initializeLayered = enabled && (value & WsExLayered) == 0;
        if (initializeLayered) value |= WsExLayered;
        value = enabled ? value | WsExTransparent : value & ~WsExTransparent;
        ThrowOnFailure(SetWindowLongPtr(windowHandle, GwlExStyle, new nint(value)));
        // Initialize only a newly added layered style. Existing WPF per-pixel alpha
        // and any previously configured native opacity must remain untouched.
        if (initializeLayered && !SetLayeredWindowAttributes(windowHandle, 0, 255, 2))
        {
            int error = Marshal.GetLastPInvokeError();
            SetWindowLongPtr(windowHandle, GwlExStyle, style);
            throw new Win32Exception(error);
        }
    }

    private static void ThrowOnFailure(nint result)
    {
        int error = Marshal.GetLastPInvokeError();
        if (result == 0 && error != 0) throw new Win32Exception(error);
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(nint windowHandle, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial nint GetWindowLongPtr(nint windowHandle, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial nint SetWindowLongPtr(nint windowHandle, int index, nint newValue);
}
