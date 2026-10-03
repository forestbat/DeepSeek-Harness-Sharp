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
        foreach (var raw in text)
        {
            if (column >= grid.Width)
                break;
            // 控制字符绝不写进网格: 换行应由 WrapSingleLine 切成独立行, 落到这里说明上游漏切,
            // 写成格子会让终端在渲染时真的换行, 把后续内容顶到下一行(表现为文字互相插入/错位)。
            if (raw is '\0' or '\n' or '\r' || char.IsControl(raw))
                continue;
            var character = raw;
            var width = TerminalTextWidth.Of(character);
            // 末列画不下宽字符: 画了会让终端 auto-wrap 顶滚视口, 整行丢弃
            if (width == 2 && column + 1 >= grid.Width)
                break;
            grid[column, y] = new Cell(character, foreground, background, style);
            if (width == 2)
                grid[column + 1, y] = new Cell('\0', foreground, background, style);
            column += width;
        }
    }
}
