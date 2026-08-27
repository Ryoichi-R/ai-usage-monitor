using System.Runtime.InteropServices;

namespace AiUsageMonitor.Platform.Windows.Window;

internal enum LayerStrategy { BottomMost, TopMost, Normal }

internal static class BottomMostStrategy
{
    internal static int TaskbarCreatedMessage { get; } =
        unchecked((int)WindowInterop.RegisterWindowMessage("TaskbarCreated"));

    internal static bool Apply(nint hWnd, LayerStrategy strategy, IWindowLayerApi? api = null)
    {
        if (hWnd == 0) return false;
        try
        {
            api ??= NativeWindowLayerApi.Instance;
            nint insertAfter = strategy switch
            {
                LayerStrategy.BottomMost => WindowInterop.HWND_BOTTOM,
                LayerStrategy.TopMost => WindowInterop.HWND_TOPMOST,
                _ => WindowInterop.HWND_NOTOPMOST,
            };
            WindowPositionCallResult result = api.SetWindowPosition(
                hWnd,
                insertAfter,
                WindowInterop.SWP_NOMOVE | WindowInterop.SWP_NOSIZE | WindowInterop.SWP_NOACTIVATE);
            return result.Succeeded;
        }
        catch
        {
            return false;
        }
    }

    internal static bool RewriteWindowPosForLayer(nint windowPosPointer, LayerStrategy strategy, bool suppressRewrite, IWindowLayerApi api)
    {
        if (windowPosPointer == 0 || suppressRewrite || strategy == LayerStrategy.Normal) return false;
        WindowInterop.WINDOWPOS pos = Marshal.PtrToStructure<WindowInterop.WINDOWPOS>(windowPosPointer);
        if ((pos.flags & WindowInterop.SWP_NOZORDER) != 0) return false;
        nint replacement = strategy == LayerStrategy.BottomMost
            ? WindowInterop.HWND_BOTTOM
            : WindowInterop.HWND_TOPMOST;
        if (pos.hwndInsertAfter == replacement) return false;
        pos.hwndInsertAfter = replacement;
        Marshal.StructureToPtr(pos, windowPosPointer, false);
        return true;
    }
}
