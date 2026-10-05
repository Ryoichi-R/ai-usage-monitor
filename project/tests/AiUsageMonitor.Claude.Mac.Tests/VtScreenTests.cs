using System.Text;

namespace AiUsageMonitor.Claude.Mac.Tests;

public sealed class VtScreenTests
{
    [Fact]
    public void RevalidatedCli285ExactCsiParametersAreAccepted()
    {
        // 2026-10-06の実CLI 2.1.285 /usage経路10回で観測した、引数を含むCSI全23種。
        string[] sequences = ["10G", "19A", "19B", "1A", "20A", "20B", "29G", "2G", "2K", "<u", ">0q", ">4m", "?1004h", "?1004l", "?2004h", "?2004l", "?2031h", "?2031l", "?25h", "?u", "G", "c", "r"];
        foreach (string sequence in sequences)
        {
            var screen = new VtScreen();
            screen.Feed(Encoding.UTF8.GetBytes("\u001b[" + sequence + "ok"));
            Assert.True(screen.Complete, screen.RejectedCategory);
        }
    }

    [Theory]
    [InlineData("hello\rX", "Xello")]
    [InlineData("hello\u001b[1G\u001b[Knew", "new")]
    [InlineData("hello\u001b[2G\u001b[1K", "  llo")]
    [InlineData("hello\u001b[2K", "")]
    [InlineData("\u001b]0;title\a\u001b]133;A\u001b\\\u001b(B\u001b[?25l\u001b[?1004h\u001b[?2004l\u001b[?2031h\u001b[c\u001b[0c\u001b[>q\u001b[?u\u001b[>4;2mtext", "text")]
    // CLI 2.1.274は起動時にXTVERSIONを明示引数付き（CSI > 0 q）で問い合わせる（2026-09-27実機の診断ログ）。
    [InlineData("\u001b[>0q\u001b[c\u001b[>q[Screen Reader Mode: on via flag]", "[Screen Reader Mode: on via flag]")]
    // PoCが描画に影響しないと分類した問い合わせ・設定の各形（kitty keyboardのpush/pop/set、DA2、DECSCUSR、同期出力等）。
    [InlineData("\u001b[<u\u001b[>1u\u001b[=1;1u\u001b[>c\u001b[>0c\u001b[2 q\u001b[ q\u001b[?2026h\u001b[?1000;1006h\u001b[?12l\u001b[?1lok", "ok")]
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

    [Theory]
    [InlineData("\u001b[31m", "csi:31m")]
    [InlineData("\u001b[?1049h", "csi:?1049h")]
    [InlineData("\u001b[H", "csi:H")]
    [InlineData("\u001b[?1049;25h", "csi:?1049;25h")]
    [InlineData("\u001b[>1;x", "csi:>1;x")]
    [InlineData("\u001b[<1q", "csi:<1q")]
    [InlineData("\u001b]8;url\a", "osc:8")]
    [InlineData("\t", "c0:0x09")]
    [InlineData("\u001bP", "esc:P")]
    [InlineData("\u001b(0", "charset")]
    [InlineData("\u001b[5;2r", "csi:r-range")]
    [InlineData("\u200d", "unicode-format")]
    [InlineData("\u0301", "unicode-combining")]
    [InlineData("\u001b[-1A", "csi:1A")]
    [InlineData("\u001b[1;2;3;4;5;6;7;8;9m", "csi:1;2;3;4;5;6;7;8;~m")]
    public void RejectionRecordsOnlyTheFirstControlCategory(string input, string category)
    {
        var screen = new VtScreen();
        screen.Feed(Encoding.UTF8.GetBytes(input + "\u001b[2Jsecret text"));
        Assert.Equal(category, screen.RejectedCategory);
        Assert.DoesNotContain("secret", screen.RejectedCategory, StringComparison.Ordinal);
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
