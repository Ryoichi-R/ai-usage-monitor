using System.Runtime.InteropServices;

namespace AiUsageMonitor.Platform.Windows.Window;

internal interface IWindowLayerApi
{
    WindowStyleObservation GetExtendedStyle(nint hWnd);
    WindowPositionCallResult SetWindowPosition(nint hWnd, nint insertAfter, uint flags);
}

internal readonly record struct WindowStyleObservation(bool Succeeded, bool IsTopMost, int ErrorCode);
internal readonly record struct WindowPositionCallResult(bool Succeeded, int ErrorCode);

internal delegate nint GetWindowLongPtrDelegate(nint hWnd, int index);
internal delegate bool SetWindowPositionDelegate(nint hWnd, nint insertAfter, int x, int y, int width, int height, uint flags);

internal sealed class NativeWindowLayerApi : IWindowLayerApi
{
    public static NativeWindowLayerApi Instance { get; } = new();
    private readonly GetWindowLongPtrDelegate _getWindowLongPtr;
    private readonly SetWindowPositionDelegate _setWindowPosition;

    private NativeWindowLayerApi()
        : this(WindowInterop.GetWindowLongPtr, WindowInterop.SetWindowPos)
    {
    }

    internal NativeWindowLayerApi(GetWindowLongPtrDelegate getWindowLongPtr, SetWindowPositionDelegate setWindowPosition)
    {
        _getWindowLongPtr = getWindowLongPtr ?? throw new ArgumentNullException(nameof(getWindowLongPtr));
        _setWindowPosition = setWindowPosition ?? throw new ArgumentNullException(nameof(setWindowPosition));
    }

    public WindowStyleObservation GetExtendedStyle(nint hWnd)
    {
        Marshal.SetLastPInvokeError(0);
        nint value = _getWindowLongPtr(hWnd, WindowInterop.GWL_EXSTYLE);
        int error = Marshal.GetLastPInvokeError();
        if (value == 0 && error != 0) return new(false, false, error);
        return new(true, (value.ToInt64() & WindowInterop.WS_EX_TOPMOST) != 0, 0);
    }

    public WindowPositionCallResult SetWindowPosition(nint hWnd, nint insertAfter, uint flags)
    {
        Marshal.SetLastPInvokeError(0);
        bool succeeded = _setWindowPosition(hWnd, insertAfter, 0, 0, 0, 0, flags);
        int error = Marshal.GetLastPInvokeError();
        return new(succeeded, succeeded ? 0 : error);
    }
}
