using Dsh.Tui;

namespace Dsh.Tests;

/** 输入区软换行: 行数、光标映射、宽字符/代理对不断开。 */
public sealed class InputLayoutTests
{
    [Fact]
    public void Wrap_SplitsByDisplayWidth()
    {
        var rows = InputLayout.Wrap("abcdefghij", 4);
        Assert.Equal(3, rows.Count);
        Assert.Equal("abcd", Slice("abcdefghij", rows[0]));
        Assert.Equal("ij", Slice("abcdefghij", rows[2]));
    }

    [Fact]
    public void Wrap_KeepsWideCharactersWhole()
    {
        const string text = "中文中文中";
        var rows = InputLayout.Wrap(text, 4);
        // 每行两个汉字(宽 4), 不能把第三个汉字劈成两半。
        Assert.Equal(3, rows.Count);
        // 每行整两个汉字(显示宽度 4), 第三个汉字整体落到下一行。
        Assert.Equal(2, rows[0].Length);
        Assert.Equal(4, rows[0].Width);
        Assert.Equal(1, rows[2].Length);
        Assert.Equal(text.Length, rows.Sum(row => row.Length));
    }

    [Fact]
    public void Wrap_HonorsExplicitNewlines_AndKeepsTrailingEmptyRow()
    {
        var rows = InputLayout.Wrap("a\nb\n", 10);
        Assert.Equal(3, rows.Count);
        Assert.Equal("a", Slice("a\nb\n", rows[0]));
        Assert.Equal("b", Slice("a\nb\n", rows[1]));
        Assert.Equal(0, rows[2].Length);
    }

    [Fact]
    public void Wrap_DoesNotSplitSurrogatePairs()
    {
        var rows = InputLayout.Wrap("😀😀", 2);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(2, row.Length));
        Assert.All(rows, row => Assert.Equal(2, row.Width));
    }

    [Fact]
    public void Cursor_MapsToVisualRowAndColumn()
    {
        var caret = InputLayout.Cursor("abcdefghij", 6, 4);
        Assert.Equal(1, caret.Row);
        Assert.Equal(2, caret.Column);

        var end = InputLayout.Cursor("abcdefghij", 10, 4);
        Assert.Equal(2, end.Row);
        Assert.Equal(2, end.Column);
    }

    [Fact]
    public void Cursor_AtNewline_ReportsTheFollwingRow()
    {
        var caret = InputLayout.Cursor("ab\ncd", 3, 10);
        Assert.Equal(1, caret.Row);
        Assert.Equal(0, caret.Column);
    }

    [Fact]
    public void OffsetAt_IsTheInverseOfCursor_ForPlainText()
    {
        foreach (var offset in new[] { 0, 3, 4, 7, 10 })
        {
            var caret = InputLayout.Cursor("abcdefghij", offset, 4);
            Assert.Equal(offset, InputLayout.OffsetAt("abcdefghij", caret.Row, caret.Column, 4));
        }
    }

    [Fact]
    public void ZeroWidth_FallsBackToASingleRow()
    {
        var rows = InputLayout.Wrap("abc", 0);
        Assert.Single(rows);
        Assert.Equal(3, rows[0].Length);
    }

    private static string Slice(string text, InputLayout.VisualRow row) => text.Substring(row.Start, row.Length);
}
