namespace Dsh.Tui;

/** 网格文本写入: 处理宽字符占位与越界裁剪, ChatWindow / ChatPane / 弹层共用。 */
internal static class CellText
{
    public static void Draw(
        CellGrid grid,
        int x,
        int y,
        string text,
        AnsiColor foreground = AnsiColor.Default,
        AnsiColor background = AnsiColor.Default,
        CellStyle style = CellStyle.None)
        => Draw(grid, x, y, text.AsSpan(), foreground, background, style);

    public static void Draw(
        CellGrid grid,
        int x,
        int y,
        ReadOnlySpan<char> text,
        AnsiColor foreground = AnsiColor.Default,
        AnsiColor background = AnsiColor.Default,
        CellStyle style = CellStyle.None)
    {
        if (y < 0 || y >= grid.Height || x >= grid.Width)
            return;
        var column = Math.Max(0, x);
        foreach (var character in text)
        {
            if (column >= grid.Width)
                break;
            if (character == '\0')
                continue;
            grid[column, y] = new Cell(character, foreground, background, style);
            var width = TerminalTextWidth.Of(character);
            if (width == 2 && column + 1 < grid.Width)
                grid[column + 1, y] = new Cell('\0', foreground, background, style);
            column += width;
        }
    }
}
