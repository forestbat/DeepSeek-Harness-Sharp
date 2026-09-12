using System.Text;
using Dsh.Tui;

namespace Dsh.Tests;

public class AnsiRendererTests
{
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

    private sealed class VirtualTerminal
    {
        private readonly bool[,] _wideRightHalf;

        public VirtualTerminal(int width, int height)
        {
            Width = width;
            Height = height;
            Screen = new char[width, height];
            _wideRightHalf = new bool[width, height];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    Screen[x, y] = ' ';
        }

        public int Width { get; }

        public int Height { get; }

        public char[,] Screen { get; }

        private int CursorX { get; set; }

        private int CursorY { get; set; }

        private bool WrapPending { get; set; }

        public void Feed(string output)
        {
            var index = 0;
            while (index < output.Length)
            {
                var character = output[index];
                if (character == '\x1b')
                {
                    index = ConsumeEscape(output, index);
                    continue;
                }
                if (character == '\r')
                {
                    CursorX = 0;
                    WrapPending = false;
                    index++;
                    continue;
                }
                if (character == '\n')
                {
                    CursorY = Math.Min(Height - 1, CursorY + 1);
                    WrapPending = false;
                    index++;
                    continue;
                }
                Put(character);
                index++;
            }
        }

        private int ConsumeEscape(string output, int index)
        {
            var cursor = index + 1;
            if (cursor >= output.Length || output[cursor] != '[')
                return cursor;
            cursor++;
            var start = cursor;
            while (cursor < output.Length && !char.IsLetter(output[cursor]))
                cursor++;
            if (cursor >= output.Length)
                return cursor;
            var final = output[cursor];
            var parameters = output[start..cursor];
            cursor++;
            switch (final)
            {
                case 'H':
                {
                    var parts = parameters.Split(';');
                    var row = parts.Length > 0 && int.TryParse(parts[0], out var parsedRow) ? parsedRow : 1;
                    var column = parts.Length > 1 && int.TryParse(parts[1], out var parsedColumn) ? parsedColumn : 1;
                    CursorY = Math.Clamp(row - 1, 0, Height - 1);
                    CursorX = Math.Clamp(column - 1, 0, Width - 1);
                    WrapPending = false;
                    break;
                }
                case 'J':
                    if (parameters is "" or "2")
                    {
                        for (var y = 0; y < Height; y++)
                            for (var x = 0; x < Width; x++)
                            {
                                Screen[x, y] = ' ';
                                _wideRightHalf[x, y] = false;
                            }
                        CursorX = 0;
                        CursorY = 0;
                    }
                    break;
                case 'K':
                    for (var x = CursorX; x < Width; x++)
                    {
                        Screen[x, CursorY] = ' ';
                        _wideRightHalf[x, CursorY] = false;
                    }
                    break;
            }
            return cursor;
        }

        private void Put(char character)
        {
            if (WrapPending)
            {
                CursorX = 0;
                CursorY = Math.Min(Height - 1, CursorY + 1);
                WrapPending = false;
            }
            if (_wideRightHalf[CursorX, CursorY] && CursorX > 0)
                Screen[CursorX - 1, CursorY] = ' ';
            Screen[CursorX, CursorY] = character;
            _wideRightHalf[CursorX, CursorY] = false;
            var advance = TerminalTextWidth.IsWide(character) ? 2 : 1;
            if (advance == 2 && CursorX + 1 < Width)
            {
                Screen[CursorX + 1, CursorY] = ' ';
                _wideRightHalf[CursorX + 1, CursorY] = true;
            }
            CursorX += advance;
            if (CursorX >= Width)
            {
                CursorX = Width - 1;
                WrapPending = true;
            }
        }
    }
}
