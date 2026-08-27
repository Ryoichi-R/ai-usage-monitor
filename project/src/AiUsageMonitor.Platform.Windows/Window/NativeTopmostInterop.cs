using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace AiUsageMonitor.Platform.Windows.Window;

internal delegate void WinEventCallback(
    nint hookHandle,
    uint eventType,
    nint windowHandle,
    int objectId,
    int childId,
    uint eventThreadId,
    uint eventTime);

internal interface ITopmostInterop
{
    int LastErrorCode { get; }

    nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        uint flags,
        WinEventCallback callback);

    bool UnhookWinEvent(nint hookHandle);

    bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        uint flags);
}

[ExcludeFromCodeCoverage(
    Justification = "Thin user32 interop boundary verified by controller tests and interactive UI testing.")]
internal sealed partial class NativeTopmostInterop : ITopmostInterop
{
    public int LastErrorCode { get; private set; }

    public nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        uint flags,
        WinEventCallback callback)
    {
        nint hook = NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            0,
            callback,
            0,
            0,
            flags);
        LastErrorCode = hook == 0 ? Marshal.GetLastPInvokeError() : 0;
        return hook;
    }

    public bool UnhookWinEvent(nint hookHandle)
    {
        bool result = NativeMethods.UnhookWinEvent(hookHandle);
        LastErrorCode = result ? 0 : Marshal.GetLastPInvokeError();
        return result;
    }

    public bool SetWindowPos(
        nint windowHandle,
        nint insertAfter,
        uint flags) =>
        SetWindowPosWithError(windowHandle, insertAfter, flags);

    private bool SetWindowPosWithError(
        nint windowHandle,
        nint insertAfter,
        uint flags)
    {
        bool result = NativeMethods.SetWindowPos(windowHandle, insertAfter, 0, 0, 0, 0, flags);
        LastErrorCode = result ? 0 : Marshal.GetLastPInvokeError();
        return result;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "SetWinEventHook", SetLastError = true)]
        internal static partial nint SetWinEventHook(
            uint eventMin,
            uint eventMax,
            nint moduleHandle,
            WinEventCallback callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "UnhookWinEvent", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(nint hookHandle);

        [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowPos(
            nint windowHandle,
            nint insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);
    }
}
