using Dsh.Tui;

namespace Dsh.Tests;

public class CellGridTests
{
    [Fact]
    public void Set_And_Read_Cell_Returns_Same_Cell()
    {
        var grid = new CellGrid(3, 2);
        var cell = new Cell('x', AnsiColor.Red, AnsiColor.Blue, CellStyle.Bold);

        grid[1, 1] = cell;

        Assert.Equal(cell, grid[1, 1]);
        Assert.Equal(new Cell(), grid[0, 0]);
    }

    [Fact]
    public void Clear_Resets_All_Cells()
    {
        var grid = new CellGrid(2, 2);
        grid[0, 0] = new Cell('a', AnsiColor.White, AnsiColor.Default, CellStyle.Reverse);
        grid[1, 1] = new Cell('b');

        grid.Clear();

        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
                Assert.Equal(new Cell(), grid[x, y]);
        }
    }

    [Fact]
    public void Diff_On_Identical_Grid_Is_Empty()
    {
        var grid = new CellGrid(2, 2);
        grid[0, 0] = new Cell('a');

        Assert.Empty(grid.Diff(grid.Clone()));
    }

    [Fact]
    public void Diff_Reports_Only_Changed_Cells()
    {
        var previous = new CellGrid(3, 2);
        previous[0, 0] = new Cell('a');
        previous[2, 1] = new Cell('z');

        var current = previous.Clone();
        current[1, 0] = new Cell('b', AnsiColor.Green);
        current[2, 1] = new Cell('y');

        var changes = current.Diff(previous).ToArray();

        Assert.Equal(2, changes.Length);
        Assert.Contains(changes, change => change is { X: 1, Y: 0 } && change.Cell.Character == 'b');
        Assert.Contains(changes, change => change is { X: 2, Y: 1 } && change.Cell.Character == 'y');
    }

    [Fact]
    public void Diff_Throws_When_Dimensions_Differ()
    {
        var previous = new CellGrid(2, 2);
        var current = new CellGrid(3, 2);

        Assert.Throws<ArgumentException>(() => current.Diff(previous).ToArray());
    }

    [Fact]
    public void Clone_Is_Independent_Snapshot()
    {
        var grid = new CellGrid(2, 2);
        grid[0, 0] = new Cell('a');

        var clone = grid.Clone();
        clone[0, 0] = new Cell('b');

        Assert.Equal('a', grid[0, 0].Character);
        Assert.Equal('b', clone[0, 0].Character);
    }

    [Fact]
    public void Overwrite_Wide_Char_Placeholder_Blanks_Lead()
    {
        var grid = new CellGrid(6, 1);
        grid[2, 0] = new Cell('深');
        grid[3, 0] = new Cell('\0');

        grid[3, 0] = new Cell('│');

        Assert.Equal(' ', grid[2, 0].Character);
        Assert.Equal('│', grid[3, 0].Character);
    }

    [Fact]
    public void Overwrite_Wide_Char_Lead_Blanks_Placeholder()
    {
        var grid = new CellGrid(6, 1);
        grid[2, 0] = new Cell('深');
        grid[3, 0] = new Cell('\0');

        grid[2, 0] = new Cell('│');

        Assert.Equal('│', grid[2, 0].Character);
        Assert.Equal(' ', grid[3, 0].Character);
    }

    [Fact]
    public void Write_Wide_Char_Sequence_Keeps_Lead_And_Placeholder()
    {
        var grid = new CellGrid(6, 1);

        grid[2, 0] = new Cell('深');
        grid[3, 0] = new Cell('\0');

        Assert.Equal('深', grid[2, 0].Character);
        Assert.Equal('\0', grid[3, 0].Character);
    }

    [Fact]
    public void Overwrite_Lead_With_Wide_Char_Keeps_Placeholder_For_Rewrite()
    {
        var grid = new CellGrid(6, 1);
        grid[2, 0] = new Cell('深');
        grid[3, 0] = new Cell('\0');

        grid[2, 0] = new Cell('度');
        grid[3, 0] = new Cell('\0');

        Assert.Equal('度', grid[2, 0].Character);
        Assert.Equal('\0', grid[3, 0].Character);
    }

    /** 浮层压在 CJK 正文上时, 侧边栏分隔线与浮层边框的列位必须保持直线(逐行同列)。 */
    [Fact]
    public void Popup_Over_Cjk_Text_Keeps_Divider_And_Borders_Straight()
    {
        const int width = 60;
        const int height = 18;
        const int dividerX = 40;
        var grid = new CellGrid(width, height);
        // 空格作背景, 使「'\0' 必须是宽字符续格」成为可严格断言的不变量
        grid.Clear(new Cell(' '));
        for (var y = 0; y < height; y++)
        {
            CellText.Draw(grid, 0, y, $"{y:D2} 深处填满了中文宽字符文本, 用于模拟正文渲染");
            grid[dividerX, y] = new Cell('│', AnsiColor.Default, AnsiColor.Default, CellStyle.Dim);
        }

        PopupList.Draw(grid, new ConsoleRect(0, 0, dividerX, height), "Commands",
            ["compact", "memory", "model", "plan", "plugins"], 1);

        for (var y = 0; y < height; y++)
            Assert.Equal('│', grid[dividerX, y].Character);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var character = grid[x, y].Character;
                if (character == '\0')
                    Assert.True(TerminalTextWidth.Of(grid[x - 1, y].Character) == 2,
                        $"dangling placeholder at ({x},{y}); nearby={DumpRow(grid, y, Math.Max(0, x - 5), Math.Min(width - 1, x + 5))}");
                else if (TerminalTextWidth.Of(character) == 2 && x + 1 < width)
                    Assert.Equal('\0', grid[x + 1, y].Character);
            }
        }
    }

    private static string DumpRow(CellGrid grid, int y, int from, int to)
        => string.Join(' ', Enumerable.Range(from, to - from + 1)
            .Select(x => $"{x}:[{(grid[x, y].Character == '\0' ? "\\0" : grid[x, y].Character.ToString())}]"));
}