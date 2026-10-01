namespace Dsh.Tui;

public static class PopupList
{
    public const int MaxPopupHeight = 16;

    /** 浮层可见的候选项行数(减去标题与边框): 翻页步长。 */
    public const int PageRows = MaxPopupHeight - 2;

    /** 带说明行(参数面板)时的翻页步长: 候选可见行数 = 浮层高度 - 标题 - 边框 - 说明行。 */
    public static int PageRowsFor(int headerCount) => Math.Max(1, MaxPopupHeight - 2 - Math.Max(0, headerCount));

    public static void Draw(CellGrid grid, ConsoleRect area, string title, IReadOnlyList<string> items, int selectedIndex)
        => Draw(grid, area, title, [], items, selectedIndex);

    /**
     * 说明行 + 候选列表共用一个浮层: 说明行常驻在标题下方(参数面板), 候选在其下滚动。
     * 说明行以 "▸" 开头表示当前正在填的参数(加粗高亮), 其余按暗色绘制。
     */
    public static void Draw(
        CellGrid grid,
        ConsoleRect area,
        string title,
        IReadOnlyList<string> headerLines,
        IReadOnlyList<string> items,
        int selectedIndex)
    {
        if (area.Width <= 0 || area.Height <= 0)
            return;

        var contentWidth = items.Count == 0 ? TerminalTextWidth.Of(title) : items.Max(TerminalTextWidth.Of);
        contentWidth = Math.Max(contentWidth, TerminalTextWidth.Of(title));
        foreach (var header in headerLines)
            contentWidth = Math.Max(contentWidth, TerminalTextWidth.Of(header));
        var width = Math.Min(area.Width, contentWidth + 4);
        var maxHeight = Math.Min(MaxPopupHeight, area.Height);
        var headerCount = Math.Min(headerLines.Count, Math.Max(0, maxHeight - 3));
        var itemRows = items.Count == 0
            ? headerCount > 0 ? 0 : 1
            : Math.Min(items.Count, Math.Max(0, maxHeight - 2 - headerCount));
        var height = Math.Min(maxHeight, 2 + headerCount + itemRows);
        if (width < 4 || height < 3)
            return;

        var x = area.X + Math.Max(0, (area.Width - width) / 2);
        var y = area.Bottom - height;
        if (y < area.Y)
            y = area.Y;

        Fill(grid, x, y, width, height);
        DrawBorder(grid, x, y, width, height);
        DrawText(grid, x + 1, y, Truncate(title, width - 2), AnsiColor.BrightCyan, AnsiColor.Default, CellStyle.Bold);

        for (var row = 0; row < headerCount; row++)
        {
            var header = headerLines[row];
            var current = header.StartsWith('▸');
            DrawText(grid, x + 1, y + 1 + row, Truncate(header, width - 2),
                current ? AnsiColor.BrightCyan : AnsiColor.Default,
                AnsiColor.Default,
                current ? CellStyle.Bold : CellStyle.Dim);
        }

        var candidateTop = y + 1 + headerCount;
        if (items.Count == 0)
        {
            if (headerCount == 0)
                DrawText(grid, x + 1, candidateTop, "  (空)", AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
            return;
        }

        var visibleCount = height - 2 - headerCount;
        if (visibleCount <= 0)
            return;
        var first = Math.Clamp(selectedIndex - visibleCount + 1, 0, Math.Max(0, items.Count - visibleCount));
        for (var row = 0; row < visibleCount; row++)
        {
            var itemIndex = first + row;
            if (itemIndex >= items.Count)
                break;
            var selected = itemIndex == selectedIndex;
            var text = $"{(selected ? "› " : "  ")}{Truncate(items[itemIndex], width - 4)}";
            DrawText(grid, x + 1, candidateTop + row, text,
                selected ? AnsiColor.Black : AnsiColor.Default,
                selected ? AnsiColor.BrightCyan : AnsiColor.Default,
                selected ? CellStyle.Bold : CellStyle.None);
        }
    }

    private static void Fill(CellGrid grid, int x, int y, int width, int height)
    {
        for (var row = 0; row < height; row++)
        {
            var targetY = y + row;
            if (targetY < 0 || targetY >= grid.Height)
                continue;
            for (var column = 0; column < width; column++)
            {
                var targetX = x + column;
                if (targetX < 0 || targetX >= grid.Width)
                    continue;
                grid[targetX, targetY] = new Cell(' ');
            }
        }
    }

    private static void DrawBorder(CellGrid grid, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0 || x >= grid.Width || y >= grid.Height)
            return;
        for (var column = 0; column < width; column++)
        {
            if (column == 0 || column == width - 1)
            {
                Set(grid, x + column, y, column == 0 ? '┌' : '┐');
                Set(grid, x + column, y + height - 1, column == 0 ? '└' : '┘');
            }
            else
            {
                Set(grid, x + column, y, '─');
                Set(grid, x + column, y + height - 1, '─');
            }
        }
        for (var row = 1; row < height - 1; row++)
        {
            Set(grid, x, y + row, '│');
            Set(grid, x + width - 1, y + row, '│');
        }
    }

    private static void Set(CellGrid grid, int x, int y, char character)
    {
        if (x < 0 || x >= grid.Width || y < 0 || y >= grid.Height)
            return;
        grid[x, y] = new Cell(character, AnsiColor.BrightCyan);
    }

    private static void DrawText(CellGrid grid, int x, int y, string text, AnsiColor foreground, AnsiColor background, CellStyle style)
    {
        if (y < 0 || y >= grid.Height)
            return;
        var column = Math.Max(0, x);
        foreach (var character in text)
        {
            if (column >= grid.Width)
                break;
            grid[column, y] = new Cell(character, foreground, background, style);
            var width = TerminalTextWidth.Of(character);
            if (width == 2 && column + 1 < grid.Width)
                grid[column + 1, y] = new Cell('\0', foreground, background, style);
            column += width;
        }
    }

    private static string Truncate(string text, int maxWidth)
    {
        if (maxWidth <= 0)
            return "";
        if (TerminalTextWidth.Of(text) <= maxWidth)
            return text;
        var width = 0;
        var index = 0;
        while (index < text.Length)
        {
            var characterWidth = TerminalTextWidth.Of(text[index]);
            if (width + characterWidth > maxWidth - 1)
                break;
            width += characterWidth;
            index++;
        }

        return $"{text[..index]}…";
    }
}
