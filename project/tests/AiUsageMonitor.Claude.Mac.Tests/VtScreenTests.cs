using System.Text;

namespace AiUsageMonitor.Claude.Mac.Tests;

public sealed class VtScreenTests
{
    [Theory]
    [InlineData("hello\rX", "Xello")]
    [InlineData("hello\u001b[1G\u001b[Knew", "new")]
    [InlineData("hello\u001b[2G\u001b[1K", "  llo")]
    [InlineData("hello\u001b[2K", "")]
    [InlineData("\u001b]0;title\a\u001b]133;A\u001b\\\u001b(B\u001b[?25l\u001b[?1004h\u001b[?2004l\u001b[?2031h\u001b[c\u001b[0c\u001b[>q\u001b[?u\u001b[>4;2mtext", "text")]
    [InlineData("\u001b7one\u001b8X", "Xne")]
    [InlineData("日e\u0301本", "日e\u0301本")]
    public void AllByteSplitsRestoreSameScreen(string text, string expected)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        for (int split = 0; split <= bytes.Length; split++)
        {
            var screen = new VtScreen();
            screen.Feed(bytes.AsSpan(0, split)); screen.Feed(bytes.AsSpan(split));
            Assert.True(screen.Complete); Assert.Equal(expected, screen.Snapshot().Lines[0]);
        }
        var single = new VtScreen();
        foreach (byte value in bytes) single.Feed(new[] { value });
        Assert.Equal(expected, single.Snapshot().Lines[0]);
    }

    [Theory]
    [InlineData("\u001b[2J")]
    [InlineData("\u001b[H")]
    [InlineData("\u001b[31m")]
    [InlineData("\u001b[?1049h")]
    [InlineData("\u001b[?7l")]
    [InlineData("\u001b]8;url\a")]
    [InlineData("\t")]
    [InlineData("\u001bP")]
    [InlineData("\u001b(0")]
    [InlineData("\u001b[1;2A")]
    [InlineData("\u001b[3K")]
    [InlineData("\u001b[-1A")]
    [InlineData("\u001b[999999999999G")]
    [InlineData("\u001b[5;2r")]
    [InlineData("\u200d")]
    [InlineData("\U0001FAE9")]
    [InlineData("\u0301")]
    public void UnknownOrInvalidControlPoisonsSession(string input)
    {
        var screen = new VtScreen(); screen.Feed(Encoding.UTF8.GetBytes(input));
        Assert.False(screen.Valid); screen.Feed(Encoding.UTF8.GetBytes("Current session"));
        Assert.False(screen.Complete); Assert.Throws<InvalidOperationException>(() => screen.Snapshot());
    }

    [Fact]
    public void IncompleteAndMalformedInputNeverProducesScreen()
    {
        var screen = new VtScreen(); screen.Feed(new byte[] { 0xE6 }); Assert.False(screen.Complete);
        screen.Feed(new byte[] { 0x97, 0xA5 }); Assert.True(screen.Complete);
        screen.Feed(new byte[] { 0x1b, 0x5b }); Assert.False(screen.Complete);
        screen.Feed(new byte[] { (byte)'K' }); Assert.True(screen.Complete);
        screen.Feed(new byte[] { 0xff }); Assert.False(screen.Valid);
        var overlong = new VtScreen(); overlong.Feed(new byte[] { 0xc0, 0x80 }); Assert.False(overlong.Valid);
        var broken = new VtScreen(); broken.Feed(new byte[] { 0xe6, 0x41 }); Assert.False(broken.Valid);
    }

    [Fact]
    public void ScrollMarginsCursorMovesAndWideOverwritesRespectCells()
    {
        var screen = new VtScreen(4, 3);
        screen.Feed(Encoding.UTF8.GetBytes("abcdE\r\nZ\r\nQ"));
        Assert.Equal("E", screen.Snapshot().Lines[0]);
        Assert.Equal("Z", screen.Snapshot().Lines[1]);
        Assert.Equal("Q", screen.Snapshot().Lines[2]);
        screen.Feed(Encoding.UTF8.GetBytes("\u001b[2;3r\u001b[1B日\u001b[2GX"));
        Assert.Equal(" X", screen.Snapshot().Lines[1]);
        screen.Feed(Encoding.UTF8.GetBytes("\u001b[99B\u001b[99A\u001b[1G😀"));
        Assert.StartsWith("😀", screen.Snapshot().Lines[0], StringComparison.Ordinal);
        Assert.Equal(2, UnicodeCellWidth.Get('日')); Assert.Equal(0, UnicodeCellWidth.Get(0xfe0f)); Assert.Equal(1, UnicodeCellWidth.Get('a'));
    }
}
