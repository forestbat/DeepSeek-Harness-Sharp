using Dsh.Tui;
using Xunit;

namespace Dsh.Tests;

/** VtScreen.Render: 行缓冲的栈路径(≤256 格)与堆回退路径(>256 格), 以及按矩形宽度裁剪。 */
public class VtScreenTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(300)]
    public void Render_Copies_Line_For_Both_Buffer_Paths(int screenWidth)
    {
        var screen = new VtScreen(screenWidth, 2);
        screen.Feed("ABC"u8);
        var grid = new CellGrid(screenWidth, 2);

        screen.Render(grid, new ConsoleRect(0, 0, screenWidth, 2));

        Assert.Equal('A', grid[0, 0].Character);
        Assert.Equal('B', grid[1, 0].Character);
        Assert.Equal('C', grid[2, 0].Character);
        Assert.Equal(' ', grid[3, 0].Character);
    }

    [Fact]
    public void Render_Clamps_To_Rect_Width()
    {
        var screen = new VtScreen(40, 1);
        screen.Feed("HELLO"u8);
        var grid = new CellGrid(40, 1);

        screen.Render(grid, new ConsoleRect(0, 0, 3, 1));

        Assert.Equal('H', grid[0, 0].Character);
        Assert.Equal('E', grid[1, 0].Character);
        Assert.Equal('L', grid[2, 0].Character);
        // 第 4 格在裁剪范围外: 不应写入(保持 CellGrid 初值)
        Assert.Equal('\0', grid[3, 0].Character);
    }
}
