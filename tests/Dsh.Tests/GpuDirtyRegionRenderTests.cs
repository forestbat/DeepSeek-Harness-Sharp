using Dsh.Tui;
using OpenTK.Graphics.OpenGL;

namespace Dsh.Tests;

/** 脏行裁剪渲染的正确性: scissor 行带 + uInstanceBase 实例偏移必须只重绘脏行, 不能错行、不能擦到脏区外。 */
[Collection("RenderBench")]
public class GpuDirtyRegionRenderTests : IDisposable
{
    private const int CellW = 16;
    private const int CellH = 20;
    private const int GridW = 8;
    private const int GridH = 6;

    private static readonly AnsiColor[] RowColors =
        [AnsiColor.Red, AnsiColor.Green, AnsiColor.Blue, AnsiColor.Yellow, AnsiColor.Magenta, AnsiColor.Cyan];

    private readonly HeadlessGl _egl;
    private readonly GpuRenderCore _core;
    private readonly GlyphAtlas _atlas;

    public GpuDirtyRegionRenderTests()
    {
        _egl = HeadlessGl.Create(GridW * CellW, GridH * CellH);
        _core = new GpuRenderCore();
        _atlas = GlyphAtlas.Shared;
        _core.Initialize(_atlas);
        GL.Viewport(0, 0, GridW * CellW, GridH * CellH);
    }

    public void Dispose()
    {
        _core.Dispose();
        _egl.Dispose();
    }

    [Fact]
    public void Dirty_Band_Renders_Changed_Row_And_Leaves_Others_Untouched()
    {
        var grid = NewGrid();
        RenderFrame(grid, null);
        var baseline = Snapshot(grid);

        PaintRow(grid, 2, AnsiColor.White);
        UploadRows(grid, [(2, 1)]);
        RenderFrame(grid, [(2, 1)]);

        Assert.True(IsWhite(ReadCellPixel(3, 2)), $"脏行应为白色, 实际 {ReadCellPixel(3, 2)}");
        AssertRowsMatchBaselineExcept(baseline, 2);
    }

    [Fact]
    public void Two_Dirty_Bands_Render_Correctly()
    {
        var grid = NewGrid();
        RenderFrame(grid, null);
        var baseline = Snapshot(grid);

        PaintRow(grid, 1, AnsiColor.White);
        PaintRow(grid, 4, AnsiColor.White);
        UploadRows(grid, [(1, 1), (4, 1)]);
        RenderFrame(grid, [(1, 1), (4, 1)]);

        Assert.True(IsWhite(ReadCellPixel(3, 1)), $"第 1 行应为白色, 实际 {ReadCellPixel(3, 1)}");
        Assert.True(IsWhite(ReadCellPixel(3, 4)), $"第 4 行应为白色, 实际 {ReadCellPixel(3, 4)}");
        AssertRowsMatchBaselineExcept(baseline, 1, 4);
    }

    [Fact]
    public void Half_Screen_And_Null_Dirty_Set_Take_Full_Redraw_And_Stay_Correct()
    {
        var grid = NewGrid();
        RenderFrame(grid, null);
        var baseline = Snapshot(grid);

        for (var row = 0; row < 3; row++)
            PaintRow(grid, row, AnsiColor.White);
        UploadRows(grid, [(0, 3)]);
        RenderFrame(grid, [(0, 3)]);

        for (var row = 0; row < 3; row++)
            Assert.True(IsWhite(ReadCellPixel(3, row)), $"第 {row} 行应为白色(半屏回退整帧路径)");
        AssertRowsMatchBaselineExcept(baseline, 0, 1, 2);

        PaintRow(grid, 5, AnsiColor.White);
        UploadRows(grid, [(5, 1)]);
        RenderFrame(grid, null);
        Assert.True(IsWhite(ReadCellPixel(3, 5)), "null 脏集应整帧重绘并生效");
        Assert.Equal(baseline[4], ReadCellPixel(3, 4));
    }

    private CellGrid NewGrid()
    {
        var grid = new CellGrid(GridW, GridH);
        for (var y = 0; y < GridH; y++)
        {
            for (var x = 0; x < GridW; x++)
                grid[x, y] = new Cell(' ', AnsiColor.Default, RowColors[y]);
        }
        return grid;
    }

    private static void PaintRow(CellGrid grid, int row, AnsiColor background)
    {
        for (var x = 0; x < GridW; x++)
            grid[x, row] = new Cell(' ', AnsiColor.Default, background);
    }

    private void UploadRows(CellGrid grid, IReadOnlyList<(int Start, int Count)> rows)
    {
        var packed = new uint[GridW * GridH];
        foreach (var (start, count) in rows)
        {
            CellPacker.PackRows(grid, start, count, packed, _atlas);
            _core.UploadCells(packed, start * GridW, count * GridW);
        }
    }

    private void RenderFrame(CellGrid grid, IReadOnlyList<(int Start, int Count)>? dirtyRows)
    {
        _core.EnsureCellCapacity(GridW * GridH);
        if (dirtyRows is null)
        {
            var packed = new uint[GridW * GridH];
            CellPacker.PackRows(grid, 0, GridH, packed, _atlas);
            _core.UploadCells(packed, 0, packed.Length);
        }
        _core.RenderFrame(_atlas, grid.Width, grid.Height, dirtyRows);
        GL.Finish();
    }

    private static (int R, int G, int B)[] Snapshot(CellGrid grid)
        => [.. Enumerable.Range(0, grid.Height).Select(row => ReadCellPixel(3, row))];

    private static void AssertRowsMatchBaselineExcept((int R, int G, int B)[] baseline, params int[] changedRows)
        => Assert.All(
            Enumerable.Range(0, GridH).Where(row => !changedRows.Contains(row)),
            row => Assert.Equal(baseline[row], ReadCellPixel(3, row)));

    private static bool IsWhite((int R, int G, int B) pixel) => pixel.R > 200 && pixel.G > 200 && pixel.B > 200;

    private static (int R, int G, int B) ReadCellPixel(int cellX, int cellY)
    {
        var pixelX = (int)((cellX + 0.5f) * CellW);
        var pixelY = GridH * CellH - 1 - (int)((cellY + 0.5f) * CellH);
        var bytes = new byte[4];
        GL.ReadPixels(pixelX, pixelY, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, bytes);
        return (bytes[0], bytes[1], bytes[2]);
    }
}
