using Dsh.Tui;

namespace Dsh.Tests;

public class PopupListTests
{
    [Fact]
    public void Draw_Draws_Border_And_Highlights_Selected_Item()
    {
        var grid = new CellGrid(20, 10);
        var area = new ConsoleRect(0, 0, 20, 10);
        IReadOnlyList<string> items = ["alpha", "beta", "gamma"];

        PopupList.Draw(grid, area, "Pick", items, 1);

        Assert.Equal('┌', grid[5, 5].Character);
        Assert.Equal('┐', grid[13, 5].Character);
        Assert.Equal('│', grid[13, 7].Character);
        Assert.Equal(CellStyle.Bold, grid[7, 7].Style & CellStyle.Bold);
        Assert.Equal('›', grid[6, 7].Character);
    }

    [Fact]
    public void Draw_Clips_To_Area_Height()
    {
        var grid = new CellGrid(30, 4);
        var area = new ConsoleRect(0, 0, 30, 4);
        IReadOnlyList<string> items = Enumerable.Range(0, 20).Select(i => $"item-{i}").ToList();

        PopupList.Draw(grid, area, "Pick", items, 0);

        Assert.Equal('┌', grid[9, 0].Character);
        Assert.Equal('└', grid[9, 3].Character);
    }
}