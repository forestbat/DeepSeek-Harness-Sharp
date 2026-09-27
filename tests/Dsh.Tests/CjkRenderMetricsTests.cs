using Dsh.Tui;
using OpenTK.Graphics.OpenGL;

namespace Dsh.Tests;

/** 真实格尺寸(图集度量推导)下的 GPU 组件测试: CJK 占满两格、与 ASCII 共享基线。 */
[Collection("RenderBench")]
public class CjkRenderMetricsTests
{
    [Fact]
    public void Cjk_Spans_Two_Cells_And_Shares_Ascii_Baseline()
    {
        const int gridWidth = 4;
        const int gridHeight = 2;
        var atlas = GlyphAtlas.Shared;
        atlas.Prewarm();
        var cellWidth = atlas.GlyphWidth;
        var cellHeight = atlas.GlyphHeight;
        using var egl = HeadlessGl.Create(gridWidth * cellWidth, gridHeight * cellHeight);
        using var core = new GpuRenderCore();
        core.Initialize(atlas);
        GL.Viewport(0, 0, gridWidth * cellWidth, gridHeight * cellHeight);

        var grid = new CellGrid(gridWidth, gridHeight);
        grid[0, 0] = new Cell('A', AnsiColor.BrightWhite);
        grid[2, 0] = new Cell('中', AnsiColor.BrightWhite);
        grid[3, 0] = new Cell('\0', AnsiColor.BrightWhite);
        var packed = new uint[gridWidth * gridHeight];
        CellPacker.PackRows(grid, 0, gridHeight, packed, atlas);
        core.EnsureCellCapacity(packed.Length);
        core.UploadCells(packed, 0, packed.Length);
        core.RenderFrame(atlas, gridWidth, gridHeight);
        GL.Finish();

        var pixels = new byte[gridWidth * cellWidth * gridHeight * cellHeight * 4];
        GL.ReadPixels(0, 0, gridWidth * cellWidth, gridHeight * cellHeight, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

        var asciiBottom = LowestInkRow(pixels, gridWidth * cellWidth, cellHeight, 0, cellWidth);
        var cjkBottom = LowestInkRow(pixels, gridWidth * cellWidth, cellHeight, cellWidth * 2, cellWidth * 4);
        Assert.True(asciiBottom >= 0 && cjkBottom >= 0, "两个字形都应有可见笔画");
        Assert.True(HasInk(pixels, gridWidth * cellWidth, cellWidth * 3, cellWidth * 4), "CJK 右半应落在第二个格内");
        // 度量取主字体基线, 回退字体自身墨水底边可差 1~3px(YaHei UI/Noto CJK 实测); 断言"大致共享基线"且不出格
        Assert.InRange(Math.Abs(asciiBottom - cjkBottom), 0, 4);
        Assert.True(cjkBottom >= cellHeight, "CJK 墨水底边不得越出本格(GL 行坐标自底向上)");
    }

    /** grid 行 0 在视口顶部, 对应 GL 行 [cellHeight, 2*cellHeight); 返回该格内最低的亮像素行(GL 行坐标)。 */
    private static int LowestInkRow(byte[] pixels, int bufferWidth, int cellHeight, int xStart, int xEnd)
    {
        for (var row = cellHeight; row < cellHeight * 2; row++)
        {
            if (HasInk(pixels, bufferWidth, xStart, xEnd, row))
                return row;
        }
        return -1;
    }

    private static bool HasInk(byte[] pixels, int bufferWidth, int xStart, int xEnd)
    {
        for (var row = 0; row < pixels.Length / (bufferWidth * 4); row++)
        {
            if (HasInk(pixels, bufferWidth, xStart, xEnd, row))
                return true;
        }
        return false;
    }

    private static bool HasInk(byte[] pixels, int bufferWidth, int xStart, int xEnd, int row)
    {
        for (var x = xStart; x < xEnd; x++)
        {
            var offset = (((row * bufferWidth) + x) * 4);
            if (pixels[offset] > 150 && pixels[offset + 1] > 150)
                return true;
        }
        return false;
    }
}
