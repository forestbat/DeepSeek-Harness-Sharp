namespace Dsh.Tui;

public static class PopupList
{
    public const int MaxPopupHeight = 16;

    /** 浮层可见的候选项行数(减去标题与边框): 翻页步长。 */
    public const int PageRows = MaxPopupHeight - 2;

    /** 带说明行(参数面板)时的翻页步长: 候选可见行数 = 浮层高度 - 标题 - 边框 - 说明行。 */
    public static int PageRowsFor(int headerCount) => Math.Max(1, MaxPopupHeight - 2 - Math.Max(0, headerCount));

    /** 浮层几何: 位置、可见窗口与首个可见候选下标。 */
    public readonly record struct Window(int X, int Y, int Width, int Height, int First, int Visible, int HeaderCount);

    /** 滚动条几何: 轨道/滑块位置(供绘制与命中测试共用同一套算法)。 */
    public sealed record Scrollbar(int Column, int TrackTop, int TrackHeight, int ThumbTop, int ThumbHeight, int First, int Visible, int ItemCount)
    {
        public int MaxFirst => Math.Max(0, ItemCount - Visible);
    }

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
        int selectedIndex,
        IReadOnlyList<string>? descriptions = null,
        bool scrollbar = false)
    {
        if (area.Width <= 0 || area.Height <= 0)
            return;

        var window = WindowOf(area, headerLines.Count, items.Count, selectedIndex);
        if (window.Width < 4 || window.Height < 3)
            return;

        // 先按整幅清底, 再画边框与内容: 浮层覆盖的行既不留空白, 也不让底层正文露出。
        Fill(grid, window.X, window.Y, window.Width, window.Height);
        DrawBorder(grid, window.X, window.Y, window.Width, window.Height);
        DrawText(grid, window.X + 1, window.Y, Truncate(title, window.Width - 2), TuiTheme.Highlight, AnsiColor.Default, CellStyle.Bold);

        for (var row = 0; row < window.HeaderCount; row++)
        {
            var header = headerLines[row];
            var current = header.StartsWith('▸');
            DrawText(grid, window.X + 1, window.Y + 1 + row, Truncate(header, window.Width - 2),
                current ? TuiTheme.Highlight : AnsiColor.Default,
                AnsiColor.Default,
                current ? CellStyle.Bold : CellStyle.Dim);
        }

        var candidateTop = window.Y + 1 + window.HeaderCount;
        if (items.Count == 0)
        {
            if (window.HeaderCount == 0)
                DrawText(grid, window.X + 1, candidateTop, "  (空)", AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
            return;
        }

        var nameColumn = DescriptionColumn(items, window.Width);
        for (var row = 0; row < window.Visible; row++)
        {
            var itemIndex = window.First + row;
            if (itemIndex >= items.Count)
                break;
            var selected = itemIndex == selectedIndex;
            var marker = selected ? "› " : "  ";
            var text = descriptions is { Count: > 0 }
                ? $"{marker}{Pad(Truncate(items[itemIndex], Math.Max(1, nameColumn - 2)), nameColumn - 2)}{Truncate(DescriptionAt(descriptions, itemIndex), window.Width - nameColumn - 2)}"
                : $"{marker}{Truncate(items[itemIndex], window.Width - 4)}";
            DrawText(grid, window.X + 1, candidateTop + row, text,
                selected ? AnsiColor.Black : AnsiColor.Default,
                selected ? TuiTheme.Highlight : AnsiColor.Default,
                selected ? CellStyle.Bold : CellStyle.None);
        }

        if (scrollbar)
            DrawScrollbar(grid, ScrollbarOf(window, items.Count));
    }

    /** 计算浮层几何: 与 Draw 共用, 供鼠标命中测试使用。 */
    public static Window WindowOf(ConsoleRect area, int headerCount, int itemCount, int selectedIndex)
    {
        var width = area.Width;
        var maxHeight = Math.Min(MaxPopupHeight, area.Height);
        headerCount = Math.Min(headerCount, Math.Max(0, maxHeight - 3));
        var itemRows = itemCount == 0
            ? headerCount > 0 ? 0 : 1
            : Math.Min(itemCount, Math.Max(0, maxHeight - 2 - headerCount));
        var height = Math.Min(maxHeight, 2 + headerCount + itemRows);
        var y = Math.Max(area.Y, area.Bottom - height);
        var visible = Math.Max(0, height - 2 - headerCount);
        var first = visible <= 0 ? 0 : Math.Clamp(selectedIndex - visible + 1, 0, Math.Max(0, itemCount - visible));
        return new Window(area.X, y, width, height, first, visible, headerCount);
    }

    /** 候选超出可见窗口时的滚动条几何; 否则为 null。 */
    public static Scrollbar? ScrollbarOf(Window window, int itemCount)
    {
        if (itemCount <= window.Visible || window.Width < 6)
            return null;
        var maxFirst = Math.Max(0, itemCount - window.Visible);
        var thumb = Math.Clamp((int)Math.Round((double)window.Visible * window.Visible / itemCount), 1, window.Visible);
        var thumbTop = maxFirst == 0 ? 0 : (int)Math.Round((double)(window.Visible - thumb) * window.First / maxFirst);
        return new Scrollbar(
            window.X + window.Width - 2,
            window.Y + 1 + window.HeaderCount,
            window.Visible,
            thumbTop,
            thumb,
            window.First,
            window.Visible,
            itemCount);
    }

    private static void DrawScrollbar(CellGrid grid, Scrollbar? bar)
    {
        if (bar is null)
            return;
        for (var row = 0; row < bar.TrackHeight; row++)
        {
            var inThumb = row >= bar.ThumbTop && row < bar.ThumbTop + bar.ThumbHeight;
            Set(grid, bar.Column, bar.TrackTop + row, inThumb ? '█' : '│');
        }
    }

    /** 名字列宽: 只占浮层左半, 给右侧解说词留出空间。 */
    private static int DescriptionColumn(IReadOnlyList<string> items, int width)
    {
        var longest = 0;
        foreach (var item in items)
            longest = Math.Max(longest, TerminalTextWidth.Of(item));
        return Math.Clamp(longest + 2, 10, Math.Max(10, width / 2));
    }

    private static string DescriptionAt(IReadOnlyList<string> descriptions, int index)
        => index < descriptions.Count ? descriptions[index] : "";

    private static string Pad(string text, int width)
    {
        var pad = width - TerminalTextWidth.Of(text);
        return pad > 0 ? text + new string(' ', pad) : text;
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
        grid[x, y] = new Cell(character, TuiTheme.Highlight);
    }

    private static void DrawText(CellGrid grid, int x, int y, string text, CellColor foreground, CellColor background, CellStyle style)
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
