namespace Dsh.Tests;

/** 取证网格的列位回归: 含 CJK 的行必须保持原列位。 */
public class TuiScreenshotTests
{
    [Fact]
    public void BuildGrid_Keeps_Columns_For_Wide_Character_Rows()
    {
        const int border = 15;
        var screen = new char[border + 1, 1];
        for (var x = 0; x <= border; x++)
            screen[x, 0] = ' ';
        screen[0, 0] = '│';
        // VT 屏里宽字符占两格: 第二格是 '\0' 续格(不能重排成空格, 否则整行尾部右移)
        screen[2, 0] = '必';
        screen[3, 0] = '\0';
        screen[4, 0] = '填';
        screen[5, 0] = '\0';
        screen[border, 0] = '│';

        var grid = TuiScreenshot.BuildGrid(screen);

        Assert.Equal('│', grid[0, 0].Character);
        Assert.Equal('必', grid[2, 0].Character);
        Assert.Equal('\0', grid[3, 0].Character);
        Assert.Equal('填', grid[4, 0].Character);
        Assert.Equal('│', grid[border, 0].Character);
    }
}