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

        Assert.Equal('┌', grid[0, 5].Character);
        Assert.Equal('┐', grid[19, 5].Character);
        Assert.Equal('│', grid[19, 7].Character);
        Assert.Equal(CellStyle.Bold, grid[1, 7].Style & CellStyle.Bold);
        Assert.Equal('›', grid[1, 7].Character);
    }

    [Fact]
    public void Draw_Clips_To_Area_Height()
    {
        var grid = new CellGrid(30, 4);
        var area = new ConsoleRect(0, 0, 30, 4);
        IReadOnlyList<string> items = Enumerable.Range(0, 20).Select(i => $"item-{i}").ToList();

        PopupList.Draw(grid, area, "Pick", items, 0);

        Assert.Equal('┌', grid[0, 0].Character);
        Assert.Equal('└', grid[0, 3].Character);
    }

    /** 浮层铺满可用宽度并整幅清底: 底层文字(含宽字符)不能从浮层旁边或内部露出。 */
    [Fact]
    public void Draw_Fills_Full_Width_Blanking_Underlying_Wide_Text()
    {
        var grid = new CellGrid(20, 10);
        var area = new ConsoleRect(0, 0, 20, 10);
        for (var x = 0; x < 20; x += 2)
        {
            grid[x, 8] = new Cell('深');
            grid[x + 1, 8] = new Cell('\0');
            grid[x, 9] = new Cell('度');
            grid[x + 1, 9] = new Cell('\0');
        }

        PopupList.Draw(grid, area, "Pick", ["ab"], 0);

        Assert.Equal('›', grid[1, 8].Character);
        Assert.Equal(' ', grid[5, 8].Character);
        Assert.Equal(' ', grid[10, 8].Character);
        Assert.Equal('│', grid[0, 8].Character);
        Assert.Equal('│', grid[19, 8].Character);
    }
}