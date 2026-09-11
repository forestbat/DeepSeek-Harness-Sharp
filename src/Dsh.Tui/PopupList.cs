namespace Dsh.Tui;

public static class PopupList
{
    public const int MaxPopupHeight = 12;

    public static void Draw(CellGrid grid, ConsoleRect area, string title, IReadOnlyList<string> items, int selectedIndex)
    {
        if (area.Width <= 0 || area.Height <= 0 || items.Count == 0)
            return;

        var contentWidth = items.Count == 0 ? title.Length : items.Max(item => item.Length);
        contentWidth = Math.Max(contentWidth, title.Length);
        var width = Math.Min(area.Width, contentWidth + 4);
        var height = Math.Min(MaxPopupHeight, Math.Min(area.Height, items.Count + 2));
        if (width < 4 || height < 3)
            return;

        var x = area.X + Math.Max(0, (area.Width - width) / 2);
        var y = area.Bottom - height;
        if (y < area.Y)
            y = area.Y;

        DrawBorder(grid, x, y, width, height);
        DrawText(grid, x + 1, y, Truncate(title, width - 2), AnsiColor.BrightCyan, AnsiColor.Default, CellStyle.Bold);

        var visibleCount = height - 2;
        var first = Math.Max(0, Math.Min(selectedIndex - visibleCount + 1, items.Count - visibleCount));
        first = Math.Clamp(first, 0, Math.Max(0, items.Count - visibleCount));
        for (var row = 0; row < visibleCount; row++)
        {
            var itemIndex = first + row;
            if (itemIndex >= items.Count)
                break;
            var selected = itemIndex == selectedIndex;
            var text = $"{(selected ? "› " : "  ")}{Truncate(items[itemIndex], width - 4)}";
            DrawText(grid, x + 1, y + 1 + row, text,
                selected ? AnsiColor.Black : AnsiColor.Default,
                selected ? AnsiColor.BrightCyan : AnsiColor.Default,
                selected ? CellStyle.Bold : CellStyle.None);
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
        for (var index = 0; index < text.Length && x + index < grid.Width; index++)
        {
            if (x + index < 0)
                continue;
            grid[x + index, y] = new Cell(text[index], foreground, background, style);
        }
    }

    private static string Truncate(string text, int maxLength)
    {
        if (maxLength <= 0)
            return "";
        return text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 1)] + "…";
    }
}
