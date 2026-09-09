using Dsh.Tui;

namespace Dsh.Tests;

public class CellQuadBuilderTests
{
    [Fact]
    public void Build_ForEmptyCell_ReturnsOnlyBackgroundQuad()
    {
        var grid = new CellGrid(1, 1);

        var quads = CellQuadBuilder.Build(grid);

        Assert.Single(quads);
        Assert.False(quads[0].IsGlyph);
        Assert.Equal(TerminalColorPalette.DefaultBackground, quads[0].Color);
    }

    [Fact]
    public void Build_ForTextCell_ReturnsBackgroundAndGlyphQuads()
    {
        var grid = new CellGrid(1, 1);
        grid[0, 0] = new Cell('A', AnsiColor.Green, AnsiColor.Black);

        var quads = CellQuadBuilder.Build(grid);

        Assert.Equal(2, quads.Count);
        Assert.False(quads[0].IsGlyph);
        Assert.True(quads[1].IsGlyph);
        Assert.Equal(TerminalColorPalette.ToRgba(AnsiColor.Green), quads[1].Color);
        Assert.True(quads[1].U0 < quads[1].U1);
        Assert.True(quads[1].V0 < quads[1].V1);
    }

    [Fact]
    public void Build_ReverseStyle_SwapsForegroundAndBackground()
    {
        var grid = new CellGrid(1, 1);
        grid[0, 0] = new Cell('x', AnsiColor.Red, AnsiColor.Blue, CellStyle.Reverse);

        var quads = CellQuadBuilder.Build(grid);

        Assert.Equal(TerminalColorPalette.ToRgba(AnsiColor.Red), quads[0].Color);
        Assert.Equal(TerminalColorPalette.ToRgba(AnsiColor.Blue), quads[1].Color);
    }

    [Fact]
    public void Build_CoversEveryCell()
    {
        var grid = new CellGrid(3, 2);
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
                grid[x, y] = new Cell('a');
        }

        var quads = CellQuadBuilder.Build(grid);

        Assert.Equal(grid.Width * grid.Height * 2, quads.Count);
        Assert.All(quads, quad => Assert.Equal(1f, quad.Width));
        Assert.All(quads, quad => Assert.Equal(1f, quad.Height));
    }
}
