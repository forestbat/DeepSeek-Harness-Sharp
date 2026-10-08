using Dsh.Tui;

namespace Dsh.Tests;

/** §8 真彩: CellColor 三态、ANSI 38;2/48;2 输出、调色板槽位分配。 */
public sealed class CellColorTests
{
    [Fact]
    public void AnsiColor_Implicitly_Converts_To_Palette_Color()
    {
        CellColor color = AnsiColor.BrightCyan;

        Assert.True(color.IsPalette);
        Assert.Equal(AnsiColor.BrightCyan, color.PaletteColor);
        Assert.True(((CellColor)AnsiColor.Default).IsDefault);
    }

    [Fact]
    public void AnsiRenderer_Emits_TrueColor_Sgr()
    {
        var grid = new CellGrid(2, 1);
        grid[0, 0] = new Cell('X', CellColor.FromRgb(18, 52, 86), CellColor.FromRgb(1, 2, 3));
        var renderer = new AnsiRenderer();

        var output = renderer.Render(grid, 0, 0, forceFull: true);

        Assert.Contains("38;2;18;52;86", output);
        Assert.Contains("48;2;1;2;3", output);
    }

    [Fact]
    public void AnsiRenderer_Keeps_Palette_Sgr_For_16_Colors()
    {
        var grid = new CellGrid(1, 1);
        grid[0, 0] = new Cell('X', AnsiColor.Green);
        var renderer = new AnsiRenderer();

        var output = renderer.Render(grid, 0, 0, forceFull: true);

        Assert.Contains("32;", output);
        Assert.DoesNotContain("38;2", output);
    }

    [Fact]
    public void ColorTable_Assigns_Stable_Rgb_Slots()
    {
        var table = new CellColorTable();
        var first = table.Index(CellColor.FromRgb(10, 20, 30));
        var again = table.Index(CellColor.FromRgb(10, 20, 30));
        var other = table.Index(CellColor.FromRgb(40, 50, 60));

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.InRange(first, CellColorTable.BaseColors, CellColorTable.Capacity - 1);
    }
}
