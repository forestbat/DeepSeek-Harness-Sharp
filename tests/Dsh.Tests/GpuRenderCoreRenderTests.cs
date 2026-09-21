using Dsh.Tui;
using OpenTK.Graphics.OpenGL;

namespace Dsh.Tests;

[Collection("RenderBench")]
public class GpuRenderCoreRenderTests : IDisposable
{
    private const int CellW = 16;
    private const int CellH = 20;
    private const int GridW = 4;
    private const int GridH = 2;

    private readonly HeadlessGl _egl;
    private readonly GpuRenderCore _core;
    private readonly GlyphAtlas _atlas;

    public GpuRenderCoreRenderTests()
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
    public void Renders_Background_Color()
    {
        var grid = new CellGrid(4, 2);
        grid[1, 0] = new Cell(' ', AnsiColor.Default, AnsiColor.Red);
        RenderFull(grid);

        var pixel = ReadCellPixel(1, 0, 0.5f, 0.5f);
        Assert.True(pixel.R > 150, $"R={pixel.R}");
        Assert.True(pixel.G < 90, $"G={pixel.G}");
    }

    [Fact]
    public void Renders_Glyph_With_Foreground_Color()
    {
        var grid = new CellGrid(4, 2);
        grid[1, 1] = new Cell('A', AnsiColor.BrightGreen);
        RenderFull(grid);

        var lit = CountCellPixels(1, 1, pixel => pixel.G > 120 && pixel.G > pixel.R + 30);
        Assert.True(lit >= 8, $"绿色字形像素数 {lit}");
    }

    [Fact]
    public void Renders_Reverse_Style_Swapped()
    {
        var grid = new CellGrid(4, 2);
        grid[0, 0] = new Cell('A', AnsiColor.Red, AnsiColor.Blue, CellStyle.Reverse);

        RenderFull(grid);

        var corner = ReadCellPixel(0, 0, 0.05f, 0.05f);
        Assert.True(corner.R > 150 && corner.B < 120, $"反转后背景应为红,实际 R={corner.R} G={corner.G} B={corner.B}");
        var blueStrokes = CountCellPixels(0, 0, pixel => pixel.B > 120 && pixel.B > pixel.R + 30);
        Assert.True(blueStrokes >= 8, $"反转后字形应为蓝,蓝色像素数 {blueStrokes}");
    }

    [Fact]
    public void Wide_Character_Map_Entry_Has_Wide_Bit()
    {
        var grid = new CellGrid(1, 1);
        grid[0, 0] = new Cell('汉');
        _atlas.EnsureBaked(grid.Cells);
        Assert.True(_atlas.MapUpload['汉'] < 0, "宽字符映射值应带宽位");
    }

    [Fact]
    public void Wide_Character_Spans_Two_Cells()
    {
        var grid = new CellGrid(4, 2);
        grid[0, 0] = new Cell('汉', AnsiColor.BrightWhite);
        grid[1, 0] = new Cell('\0', AnsiColor.BrightWhite);

        RenderFull(grid);

        // 无 CJK 字体的机器上字形退化为窄 tofu,span=2 拉伸仍应把笔画送进头格右半区
        var bytes = new byte[CellW * CellH * 4];
        GL.ReadPixels(0, GridH * CellH - CellH, CellW, CellH, PixelFormat.Rgba, PixelType.UnsignedByte, bytes);
        var rightHalfLit = 0;
        for (var y = 0; y < CellH; y++)
            for (var x = CellW / 2; x < CellW; x++)
            {
                if (bytes[(y * CellW + x) * 4] > 150)
                    rightHalfLit++;
            }
        Assert.True(rightHalfLit >= 8, $"span=2 应把头格笔画拉伸到右半区,实际亮像素 {rightHalfLit}");
    }

    [Fact]
    public void Dirty_Row_Upload_Updates_Only_That_Row()
    {
        var grid = new CellGrid(4, 2);
        grid[0, 0] = new Cell('A', AnsiColor.BrightWhite);
        RenderFull(grid);
        var before = ReadCellPixel(0, 0, 0.5f, 0.25f);

        grid[1, 1] = new Cell(' ', AnsiColor.Default, AnsiColor.Red);
        var packed = new uint[4];
        CellPacker.PackRows(grid, 1, 1, packed, _atlas);
        _core.UploadCells(packed, 1 * 4, 4);
        _core.RenderFrame(_atlas, grid.Width, grid.Height);
        GL.Finish();

        var changed = ReadCellPixel(1, 1, 0.5f, 0.5f);
        Assert.True(changed.R > 150, $"脏行更新应生效,R={changed.R}");
        var untouched = ReadCellPixel(0, 0, 0.5f, 0.25f);
        Assert.Equal(before, untouched);
    }

    private void RenderFull(CellGrid grid)
    {
        var packed = new uint[grid.Width * grid.Height];
        CellPacker.PackRows(grid, 0, grid.Height, packed, _atlas);
        _core.EnsureCellCapacity(packed.Length);
        _core.UploadCells(packed, 0, packed.Length);
        _core.RenderFrame(_atlas, grid.Width, grid.Height);
        GL.Finish();
    }

    private static (int R, int G, int B) ReadCellPixel(int cellX, int cellY, float fractionX, float fractionY)
    {
        var pixelX = (int)((cellX + fractionX) * CellW);
        var pixelY = GridH * CellH - 1 - (int)((cellY + fractionY) * CellH);
        var bytes = new byte[4];
        GL.ReadPixels(pixelX, pixelY, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, bytes);
        return (bytes[0], bytes[1], bytes[2]);
    }

    private static int CountCellPixels(int cellX, int cellY, Func<(int R, int G, int B), bool> predicate)
    {
        var bytes = new byte[CellW * CellH * 4];
        GL.ReadPixels(cellX * CellW, GridH * CellH - (cellY + 1) * CellH, CellW, CellH, PixelFormat.Rgba, PixelType.UnsignedByte, bytes);
        var count = 0;
        for (var index = 0; index < bytes.Length; index += 4)
        {
            if (predicate((bytes[index], bytes[index + 1], bytes[index + 2])))
                count++;
        }
        return count;
    }
}
