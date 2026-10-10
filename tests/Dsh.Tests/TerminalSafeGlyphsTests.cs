namespace Dsh.Tests;

/** 落屏前把东亚宽度 Ambiguous 的字符换成等宽 ASCII: 见 TerminalSafeGlyphs 的说明。 */
public sealed class TerminalSafeGlyphsTests
{
    [Theory]
    [InlineData('\u2500', '-')]
    [InlineData('\u2502', '|')]
    [InlineData('\u250c', '+')]
    [InlineData('\u2510', '+')]
    [InlineData('\u2514', '+')]
    [InlineData('\u2518', '+')]
    [InlineData('\u253c', '+')]
    [InlineData('\u2588', '#')]
    [InlineData('\u2591', '.')]
    [InlineData('\u25cf', '*')]
    [InlineData('\u25c0', '<')]
    [InlineData('\u25b6', '>')]
    [InlineData('\u2190', '<')]
    [InlineData('\u2192', '>')]
    [InlineData('\u21b3', '>')]
    [InlineData('\u2713', '+')]
    [InlineData('\u2717', 'x')]
    [InlineData('\u276f', '>')]
    [InlineData('\u2026', '.')]
    [InlineData('\u2014', '-')]
    [InlineData('\u201c', '"')]
    [InlineData('\u00d7', 'x')]
    [InlineData('\u00b7', '-')]
    public void Ambiguous_Glyph_Maps_To_Ascii(char input, char expected)
        => Assert.Equal(expected, TerminalSafeGlyphs.AsciiSafe(input));

    [Fact]
    public void Ascii_Is_Unchanged()
    {
        foreach (var c in "aZ0 ~/|+-*#!?.")
            Assert.Equal(c, TerminalSafeGlyphs.AsciiSafe(c));
    }

    [Fact]
    public void Wide_Glyphs_Are_Unchanged()
    {
        // 已被宽度模型记为宽(两格)的字符与终端一致, 换掉会让两格单元对不上。
        Assert.Equal('\u4e2d', TerminalSafeGlyphs.AsciiSafe('\u4e2d'));
        Assert.Equal('\u2705', TerminalSafeGlyphs.AsciiSafe('\u2705'));
        Assert.Equal('\u26a1', TerminalSafeGlyphs.AsciiSafe('\u26a1'));
    }

    [Fact]
    public void Mapping_Never_Produces_An_Ambiguous_Or_Wide_Replacement()
    {
        for (var code = '\u00a1'; code < '\uffff'; code++)
        {
            var mapped = TerminalSafeGlyphs.AsciiSafe(code);
            if (mapped == code)
                continue;
            Assert.InRange(mapped, '\u0020', '\u007e');
        }
    }
}
