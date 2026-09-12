using System.Text;

namespace Dsh.Tui;

public sealed class AnsiRenderer
{
    private CellGrid? _previous;

    public string Render(CellGrid grid, int cursorX, int cursorY, bool forceFull = false)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var needsFull = forceFull
            || _previous is null
            || _previous.Width != grid.Width
            || _previous.Height != grid.Height;

        var builder = new StringBuilder();
        if (needsFull)
        {
            AppendFull(builder, grid);
        }
        else
        {
            AppendDiff(builder, grid, _previous!);
        }

        _previous = grid.Clone();
        AppendCursor(builder, cursorX, cursorY, grid.Width, grid.Height);
        return builder.ToString();
    }

    public void Reset()
        => _previous = null;

    private static void AppendFull(StringBuilder builder, CellGrid grid)
    {
        builder.Append("\x1b[2J\x1b[H");
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                var cell = grid[x, y];
                builder.Append(Sgr(cell));
                builder.Append(Sanitize(cell.Character));
                if (TerminalTextWidth.IsWide(cell.Character))
                    x++;
            }

            builder.Append("\x1b[0m");
            if (y < grid.Height - 1)
                builder.Append("\r\n");
        }
    }

    private static void AppendDiff(StringBuilder builder, CellGrid grid, CellGrid previous)
    {
        for (var y = 0; y < grid.Height; y++)
        {
            if (RowEqual(grid, previous, y))
                continue;
            AppendPosition(builder, 0, y);
            var x = 0;
            var runOpen = false;
            AnsiColor runForeground = default;
            AnsiColor runBackground = default;
            CellStyle runStyle = default;
            while (x < grid.Width)
            {
                var cell = grid[x, y];
                if (cell.Character == '\0' && x > 0 && TerminalTextWidth.IsWide(grid[x - 1, y].Character))
                {
                    x++;
                    continue;
                }

                if (!runOpen || cell.Foreground != runForeground || cell.Background != runBackground || cell.Style != runStyle)
                {
                    if (runOpen)
                        builder.Append("\x1b[0m");
                    builder.Append(Sgr(cell));
                    (runForeground, runBackground, runStyle) = (cell.Foreground, cell.Background, cell.Style);
                    runOpen = true;
                }
                builder.Append(Sanitize(cell.Character));
                x++;
            }
            builder.Append("\x1b[0m");
        }
    }

    private static bool RowEqual(CellGrid grid, CellGrid previous, int y)
    {
        for (var x = 0; x < grid.Width; x++)
        {
            if (grid[x, y] != previous[x, y])
                return false;
        }
        return true;
    }

    private static void AppendPosition(StringBuilder builder, int x, int y)
    {
        builder.Append("\x1b[");
        builder.Append(y + 1);
        builder.Append(';');
        builder.Append(x + 1);
        builder.Append('H');
    }

    private static void AppendCursor(StringBuilder builder, int cursorX, int cursorY, int width, int height)
    {
        cursorX = Math.Clamp(cursorX, 0, width - 1);
        cursorY = Math.Clamp(cursorY, 0, height - 1);
        AppendPosition(builder, cursorX, cursorY);
    }

    private static string Sgr(Cell cell)
    {
        var codes = new List<int>(4);
        if ((cell.Style & CellStyle.Bold) != 0)
            codes.Add(1);
        if ((cell.Style & CellStyle.Dim) != 0)
            codes.Add(2);
        if ((cell.Style & CellStyle.Reverse) != 0)
            codes.Add(7);
        codes.Add(ForegroundCode(cell.Foreground));
        codes.Add(BackgroundCode(cell.Background));
        return $"\x1b[{string.Join(';', codes)}m";
    }

    private static int ForegroundCode(AnsiColor color)
    {
        if (color == AnsiColor.Default)
            return 39;
        if (color >= AnsiColor.BrightBlack)
            return 90 + (color - AnsiColor.BrightBlack);
        return 30 + (color - AnsiColor.Black);
    }

    private static int BackgroundCode(AnsiColor color)
    {
        if (color == AnsiColor.Default)
            return 49;
        if (color >= AnsiColor.BrightBlack)
            return 100 + (color - AnsiColor.BrightBlack);
        return 40 + (color - AnsiColor.Black);
    }

    private static char Sanitize(char value)
        => value == '\0' ? ' ' : value;
}