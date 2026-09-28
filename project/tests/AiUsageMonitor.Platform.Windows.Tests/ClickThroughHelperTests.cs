using System.Runtime.InteropServices;
using AiUsageMonitor.Platform.Windows.Window;

namespace AiUsageMonitor.Platform.Windows.Tests;

public sealed class ClickThroughHelperTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(0x00200000u)] // Avalonia composition: WS_EX_NOREDIRECTIONBITMAP
    public void NonLayeredWindowCanTogglePassthroughWithoutLosingOtherStyles(uint initialStyle)
    {
        nint window = CreateWindowEx(initialStyle, "STATIC", "", 0x80000000u, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, window);
        try
        {
            ClickThroughHelper.Apply(window, true);
            long enabled = GetWindowLongPtr(window, -20).ToInt64();
            Assert.Equal(0x80020L, enabled & 0x80020L);
            Assert.Equal((long)initialStyle, enabled & initialStyle);
            Assert.True(GetLayeredWindowAttributes(window, out _, out byte alpha, out uint flags));
            Assert.Equal(255, alpha);
            Assert.Equal(2u, flags);

            ClickThroughHelper.Apply(window, false);
            Assert.Equal(0L, GetWindowLongPtr(window, -20).ToInt64() & 0x20);
            ClickThroughHelper.Apply(window, true);
            Assert.Equal(enabled, GetWindowLongPtr(window, -20).ToInt64());
        }
        finally { DestroyWindow(window); }
    }

    [Fact]
    public void ExistingLayeredWindowKeepsItsAlphaAndColorKey()
    {
        nint window = CreateWindowEx(0x80000, "STATIC", "", 0x80000000u, 0, 0, 10, 10, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, window);
        try
        {
            Assert.True(SetLayeredWindowAttributes(window, 0x123456, 123, 3));
            foreach (bool enabled in new[] { true, false, true })
            {
                ClickThroughHelper.Apply(window, enabled);
                Assert.True(GetLayeredWindowAttributes(window, out uint key, out byte alpha, out uint flags));
                Assert.Equal(0x123456u, key);
                Assert.Equal(123, alpha);
                Assert.Equal(3u, flags);
            }
        }
        finally { DestroyWindow(window); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint key, byte alpha, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(nint window, out uint key, out byte alpha, out uint flags);
}
