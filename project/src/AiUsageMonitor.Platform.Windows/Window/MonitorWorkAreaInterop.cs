using System.Runtime.InteropServices;

namespace AiUsageMonitor.Platform.Windows.Window;

internal readonly record struct MonitorPixelRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Math.Max(0, Right - Left);
    internal int Height => Math.Max(0, Bottom - Top);
}

internal readonly record struct NativeMonitorInfo(
    nint Handle,
    string DeviceName,
    MonitorPixelRect WorkingArea,
    bool IsPrimary = false);

internal readonly record struct MonitorWorkAreaSnapshot(
    nint Handle,
    string DeviceName,
    MonitorPixelRect WorkingArea,
    uint DpiX,
    uint DpiY,
    string? StableId = null);

internal readonly record struct MonitorResolutionResult(
    bool Succeeded,
    MonitorWorkAreaSnapshot Snapshot,
    string? FailureStage,
    int ErrorCode)
{
    internal static MonitorResolutionResult Failure(string stage, int errorCode = 0) =>
        new(false, default, stage, errorCode);

    internal static MonitorResolutionResult Success(MonitorWorkAreaSnapshot snapshot) =>
        new(true, snapshot, null, 0);
}

internal interface IMonitorNativeApi
{
    bool TryEnumerateMonitors(ICollection<nint> handles, out int errorCode);

    bool TryGetMonitorInfo(nint monitor, out NativeMonitorInfo info, out int errorCode);

    bool TryGetEffectiveDpi(nint monitor, out uint dpiX, out uint dpiY, out int hresult);

    nint MonitorFromWindow(nint windowHandle);

    nint MonitorFromPoint(int x, int y);

    /// <summary>GDI device名（DISPLAYn）に接続中のモニタのdevice interface path。取得できなければnull。</summary>
    string? GetStableId(string deviceName);
}

internal sealed class MonitorWorkAreaResolver
{
    private readonly IMonitorNativeApi _native;

    internal MonitorWorkAreaResolver(IMonitorNativeApi? native = null) =>
        _native = native ?? NativeMonitorApi.Instance;

    internal MonitorResolutionResult Resolve(
        string? savedDeviceName,
        nint windowHandle,
        (int X, int Y)? centerPointPixels = null,
        bool preferCenterPoint = false,
        string? savedStableId = null)
    {
        var handles = new List<nint>();
        if (!_native.TryEnumerateMonitors(handles, out int enumError) || handles.Count == 0)
            return MonitorResolutionResult.Failure("EnumDisplayMonitors", enumError);

        NativeMonitorInfo? saved = null;
        string? savedInfoStableId = null;
        var infos = new List<NativeMonitorInfo>(handles.Count);
        foreach (nint handle in handles)
        {
            if (!_native.TryGetMonitorInfo(handle, out NativeMonitorInfo info, out int infoError))
                return MonitorResolutionResult.Failure("GetMonitorInfo", infoError);
            infos.Add(info);
            if (saved is not null) continue;
            if (savedStableId is not null)
            {
                // DISPLAYnは接続変更で別モニタへ振り直されるため、固有IDがある場合は名前で照合しない。
                string? stableId = _native.GetStableId(info.DeviceName);
                if (string.Equals(savedStableId, stableId, StringComparison.OrdinalIgnoreCase))
                {
                    saved = info;
                    savedInfoStableId = stableId;
                }
            }
            else if (savedDeviceName is not null &&
                string.Equals(savedDeviceName, info.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                saved = info;
            }
        }

        NativeMonitorInfo selected;
        if (saved is { } savedInfo)
        {
            selected = savedInfo;
        }
        else if (!preferCenterPoint && infos.FirstOrDefault(info => info.IsPrimary) is { Handle: not 0 } primary)
        {
            // 自動指定、または保存済みdeviceが消失した場合は、設定画面の「自動（プライマリ）」表記どおり
            // プライマリへ戻す。ウィンドウ最寄りへ戻すと、DISPLAY番号の振り直し後に非プライマリの隅へ
            // 表示され続け、起動していないように見える。
            selected = primary;
        }
        else
        {
            nint nearest = preferCenterPoint && centerPointPixels is { } point
                ? _native.MonitorFromPoint(point.X, point.Y)
                : windowHandle != 0
                    ? _native.MonitorFromWindow(windowHandle)
                    : centerPointPixels is { } fallbackPoint
                        ? _native.MonitorFromPoint(fallbackPoint.X, fallbackPoint.Y)
                        : 0;
            selected = infos.FirstOrDefault(info => info.Handle == nearest);
            if (selected.Handle == 0)
                selected = infos[0];
        }

        if (!_native.TryGetEffectiveDpi(selected.Handle, out uint dpiX, out uint dpiY, out int dpiError) ||
            dpiX == 0 || dpiY == 0)
            return MonitorResolutionResult.Failure("GetScaleFactorForMonitor", dpiError);

        return MonitorResolutionResult.Success(new MonitorWorkAreaSnapshot(
            selected.Handle,
            selected.DeviceName,
            selected.WorkingArea,
            dpiX,
            dpiY,
            savedInfoStableId ?? _native.GetStableId(selected.DeviceName)));
    }

    internal MonitorResolutionResult ResolveForDrag(
        nint windowHandle,
        (int X, int Y) centerPointPixels) =>
        Resolve(null, windowHandle, centerPointPixels, preferCenterPoint: true);
}

internal sealed unsafe class NativeMonitorApi : IMonitorNativeApi
{
    internal static NativeMonitorApi Instance { get; } = new();

    private NativeMonitorApi() { }

    public bool TryEnumerateMonitors(ICollection<nint> handles, out int errorCode)
    {
        ArgumentNullException.ThrowIfNull(handles);
        var context = new EnumContext(handles);
        GCHandle contextHandle = GCHandle.Alloc(context);
        try
        {
            int result = NativeMethods.EnumDisplayMonitors(
                0,
                0,
                &MonitorEnumCallback,
                GCHandle.ToIntPtr(contextHandle));
            errorCode = result != 0 ? 0 : Marshal.GetLastPInvokeError();
            return result != 0 && !context.Failed;
        }
        finally
        {
            contextHandle.Free();
        }
    }

    public bool TryGetMonitorInfo(nint monitor, out NativeMonitorInfo info, out int errorCode)
    {
        var nativeInfo = new NativeMethods.MONITORINFOEX
        {
            CbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
        };
        bool result = NativeMethods.GetMonitorInfo(monitor, ref nativeInfo);
        errorCode = result ? 0 : Marshal.GetLastPInvokeError();
        if (!result)
        {
            info = default;
            return false;
        }

        info = new NativeMonitorInfo(
            monitor,
            nativeInfo.DeviceName,
            new MonitorPixelRect(
                nativeInfo.Work.Left,
                nativeInfo.Work.Top,
                nativeInfo.Work.Right,
                nativeInfo.Work.Bottom),
            (nativeInfo.Flags & NativeMethods.MonitorInfoPrimary) != 0);
        return true;
    }

    public bool TryGetEffectiveDpi(nint monitor, out uint dpiX, out uint dpiY, out int hresult)
    {
        hresult = NativeMethods.GetScaleFactorForMonitor(monitor, out uint scalePercent);
        if (hresult < 0 || scalePercent == 0)
        {
            dpiX = 0;
            dpiY = 0;
            return false;
        }

        uint effectiveDpi = checked((uint)Math.Round(
            96d * scalePercent / 100d,
            MidpointRounding.AwayFromZero));
        dpiX = effectiveDpi;
        dpiY = effectiveDpi;
        return true;
    }

    public nint MonitorFromWindow(nint windowHandle) =>
        NativeMethods.MonitorFromWindow(windowHandle, NativeMethods.MonitorDefaultToNearest);

    public nint MonitorFromPoint(int x, int y) =>
        NativeMethods.MonitorFromPoint(new NativeMethods.POINT { X = x, Y = y }, NativeMethods.MonitorDefaultToNearest);

    public string? GetStableId(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return null;
        string? firstAttached = null;
        for (uint index = 0; ; index++)
        {
            var device = new NativeMethods.DISPLAY_DEVICE
            {
                Cb = (uint)Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>(),
            };
            if (!NativeMethods.EnumDisplayDevices(deviceName, index, ref device, NativeMethods.EddGetDeviceInterfaceName))
                break;
            if (string.IsNullOrWhiteSpace(device.DeviceID)) continue;
            // 複製表示では1つのDISPLAYnに複数モニタが属する。稼働中のモニタを優先する。
            if ((device.StateFlags & NativeMethods.DisplayDeviceActive) != 0) return device.DeviceID;
            firstAttached ??= device.DeviceID;
        }
        return firstAttached;
    }

    private sealed class EnumContext
    {
        internal EnumContext(ICollection<nint> handles) => Handles = handles;
        internal ICollection<nint> Handles { get; }
        internal bool Failed { get; set; }
    }

    [UnmanagedCallersOnly]
    private static int MonitorEnumCallback(nint monitor, nint _, nint __, nint data)
    {
        try
        {
            var context = (EnumContext)GCHandle.FromIntPtr(data).Target!;
            context.Handles.Add(monitor);
            return 1;
        }
        catch
        {
            try
            {
                var context = (EnumContext)GCHandle.FromIntPtr(data).Target!;
                context.Failed = true;
            }
            catch { }
            return 0;
        }
    }

    private static partial class NativeMethods
    {
        internal const uint MonitorDefaultToNearest = 2;
        internal const uint MonitorInfoPrimary = 0x00000001;
        internal const uint EddGetDeviceInterfaceName = 0x00000001;
        internal const uint DisplayDeviceActive = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            internal int X;
            internal int Y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct MONITORINFOEX
        {
            internal uint CbSize;
            internal RECT Monitor;
            internal RECT Work;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string DeviceName;
        }

        [DllImport("user32.dll", EntryPoint = "EnumDisplayMonitors", SetLastError = true)]
        internal static extern int EnumDisplayMonitors(
            nint hdc,
            nint clipRect,
            delegate* unmanaged<nint, nint, nint, nint, int> callback,
            nint data);

        [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(nint monitor, ref MONITORINFOEX info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct DISPLAY_DEVICE
        {
            internal uint Cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            internal string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string DeviceString;
            internal uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            internal string DeviceKey;
        }

        [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayDevices(
            string deviceName,
            uint deviceIndex,
            ref DISPLAY_DEVICE displayDevice,
            uint flags);

        [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
        internal static extern nint MonitorFromWindow(nint window, uint flags);

        [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
        internal static extern nint MonitorFromPoint(POINT point, uint flags);

        [DllImport("Shcore.dll", EntryPoint = "GetScaleFactorForMonitor")]
        internal static extern int GetScaleFactorForMonitor(
            nint monitor,
            out uint scalePercent);
    }
}

internal static class NativeWindowPositioner
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    internal static bool TryGetBounds(
        nint windowHandle,
        out MonitorPixelRect bounds,
        out int errorCode)
    {
        bool succeeded = NativeMethods.GetWindowRect(windowHandle, out NativeMethods.RECT rect);
        errorCode = succeeded ? 0 : Marshal.GetLastPInvokeError();
        bounds = succeeded
            ? new MonitorPixelRect(rect.Left, rect.Top, rect.Right, rect.Bottom)
            : default;
        return succeeded;
    }

    internal static bool TrySetPosition(
        nint windowHandle,
        int left,
        int top,
        out int errorCode)
    {
        bool succeeded = NativeMethods.SetWindowPos(
            windowHandle,
            0,
            left,
            top,
            0,
            0,
            SwpNoSize | SwpNoZOrder | SwpNoActivate);
        errorCode = succeeded ? 0 : Marshal.GetLastPInvokeError();
        return succeeded;
    }

    private static partial class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct RECT
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint windowHandle, out RECT rect);

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
