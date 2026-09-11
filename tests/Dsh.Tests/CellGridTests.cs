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
}