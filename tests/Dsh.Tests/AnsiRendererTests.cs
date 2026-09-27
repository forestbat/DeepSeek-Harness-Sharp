using Dsh.Tui;

namespace Dsh.Tests;

public class AnsiRendererTests
{
    [Fact]
    public void RenderToBuffer_Matches_String_Render()
    {
        var frames = new List<CellGrid>();
        var baseGrid = new CellGrid(40, 8);
        WriteText(baseGrid, 0, 1, "第一帧：包含中文与 ASCII 的内容");
        frames.Add(baseGrid);
        var edited = baseGrid.Clone();
        WriteText(edited, 0, 3, "diff 帧");
        frames.Add(edited);
        var styled = edited.Clone();
        for (var x = 0; x < 20; x++)
            styled[x, 5] = new Cell('▀', AnsiColor.BrightCyan, AnsiColor.Black, CellStyle.Bold);
        frames.Add(styled);

        var stringRenderer = new AnsiRenderer();
        var bufferRenderer = new AnsiRenderer();
        foreach (var (index, grid) in frames.Index())
        {
            var expected = stringRenderer.Render(grid, 0, 0, forceFull: index == 0);
            var actual = bufferRenderer.RenderToBuffer(grid, 0, 0, forceFull: index == 0);
            Assert.Equal(expected, actual.Span.ToString());
        }
    }

    [Fact]
    public void Diff_After_Popup_Close_Over_Cjk_Line_Leaves_No_Remnants()
    {
        const int width = 60;
        const int height = 10;
        var baseGrid = new CellGrid(width, height);
        WriteText(baseGrid, 0, 2, "你好世界，这是一行用于测试的中文文本，会自动换行并对齐。");
        WriteText(baseGrid, 0, 4, "another line");

        var popupGrid = baseGrid.Clone();
        DrawBox(popupGrid, 10, 1, 20, 5);

        var closedGrid = baseGrid.Clone();

        var terminal = new VirtualTerminal(width, height);
        var renderer = new AnsiRenderer();
        terminal.Feed(renderer.Render(baseGrid, 0, 0, forceFull: true));
        terminal.Feed(renderer.Render(popupGrid, 0, 0));
        terminal.Feed(renderer.Render(closedGrid, 0, 0));

        AssertScreenMatches(terminal, closedGrid);
    }

    [Fact]
    public void Diff_Row_Shortening_Erases_Trailing_Cells()
    {
        const int width = 40;
        const int height = 6;
        var longGrid = new CellGrid(width, height);
        WriteText(longGrid, 0, 1, "一行很长的中文内容需要被截短显示测试");

        var shortGrid = new CellGrid(width, height);
        WriteText(shortGrid, 0, 1, "短行");

        var terminal = new VirtualTerminal(width, height);
        var renderer = new AnsiRenderer();
        terminal.Feed(renderer.Render(longGrid, 0, 0, forceFull: true));
        terminal.Feed(renderer.Render(shortGrid, 0, 0));

        AssertScreenMatches(terminal, shortGrid);
    }

    [Fact]
    public void Diff_Sidebar_Border_Move_Leaves_No_Remnants()
    {
        const int width = 50;
        const int height = 8;
        var leftBorder = new CellGrid(width, height);
        for (var y = 0; y < height; y++)
            leftBorder[20, y] = new Cell('│');

        var rightBorder = new CellGrid(width, height);
        for (var y = 0; y < height; y++)
            rightBorder[30, y] = new Cell('│');

        var terminal = new VirtualTerminal(width, height);
        var renderer = new AnsiRenderer();
        terminal.Feed(renderer.Render(leftBorder, 0, 0, forceFull: true));
        terminal.Feed(renderer.Render(rightBorder, 0, 0));

        AssertScreenMatches(terminal, rightBorder);
    }

    [Fact]
    public void Unchanged_Frame_Emits_Only_Sync_Markers()
    {
        var grid = new CellGrid(40, 8);
        WriteText(grid, 0, 1, "static content");
        var renderer = new AnsiRenderer();
        renderer.Render(grid, 3, 2, forceFull: true);

        var second = renderer.Render(grid, 3, 2);

        Assert.Equal("\x1b[?2026h\x1b[?2026l", second);
    }

    [Fact]
    public void Style_State_Machine_Emits_Fullwidth_Style_Once_Per_Frame()
    {
        var grid = new CellGrid(40, 8);
        for (var y = 0; y < 8; y++)
            for (var x = 0; x < 40; x++)
                grid[x, y] = new Cell('a', AnsiColor.Red, AnsiColor.Default, CellStyle.Bold);
        var renderer = new AnsiRenderer();

        var frame = renderer.Render(grid, 0, 0, forceFull: true);

        Assert.Equal(1, CountOccurrences(frame, "\x1b[1;31;49m"));
        Assert.Equal(1, CountOccurrences(frame, "\x1b[0m"));
        Assert.Equal("\x1b[?2026h\x1b[?2026l", renderer.Render(grid, 0, 0));
    }

    [Fact]
    public void Diff_Style_Change_Emits_Only_Delta_Attributes()
    {
        var grid = new CellGrid(20, 4);
        WriteText(grid, 0, 0, "aaaa");
        var renderer = new AnsiRenderer();
        renderer.Render(grid, 0, 0, forceFull: true);

        grid[1, 0] = new Cell('a', AnsiColor.Default, AnsiColor.Default, CellStyle.Bold);
        var diff = renderer.Render(grid, 0, 0);

        Assert.Contains("\x1b[1m", diff);
        Assert.DoesNotContain("39", diff);
    }

    [Fact]
    public void Cursor_Unchanged_Is_Not_Reemitted()
    {
        var grid = new CellGrid(20, 4);
        var renderer = new AnsiRenderer();
        renderer.Render(grid, 5, 2, forceFull: true);
        WriteText(grid, 0, 0, "x");

        var diff = renderer.Render(grid, 5, 2);

        Assert.Equal(1, CountOccurrences(diff, "H"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static void WriteText(CellGrid grid, int x, int y, string text)
    {
        foreach (var character in text)
        {
            if (x >= grid.Width)
                break;
            grid[x, y] = new Cell(character);
            x += TerminalTextWidth.IsWide(character) ? 2 : 1;
        }
    }

    private static void DrawBox(CellGrid grid, int left, int top, int right, int bottom)
    {
        for (var x = left; x <= right; x++)
        {
            grid[x, top] = new Cell('─');
            grid[x, bottom] = new Cell('─');
        }
        for (var y = top; y <= bottom; y++)
        {
            grid[left, y] = new Cell('│');
            grid[right, y] = new Cell('│');
        }
        grid[left, top] = new Cell('┌');
        grid[right, top] = new Cell('┐');
        grid[left, bottom] = new Cell('└');
        grid[right, bottom] = new Cell('┘');
    }

    private static void AssertScreenMatches(VirtualTerminal terminal, CellGrid grid)
    {
        var mismatches = new List<string>();
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                var expected = grid[x, y].Character is '\0' ? ' ' : grid[x, y].Character;
                var actual = terminal.Screen[x, y];
                if (expected != actual)
                    mismatches.Add($"({x},{y}): expected '{expected}' but was '{actual}'");
            }
        }
        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches.Take(10)));
    }

}
