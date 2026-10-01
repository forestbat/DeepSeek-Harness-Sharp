using Dsh.Tui;
using OpenTK.Graphics.OpenGL;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tests;

/** 字符屏 → 工程自身字形图集/GL 管线 → PNG 的取证工具(测试只往工程内写文件)。 */
internal static class TuiScreenshot
{
    public static string ArtifactPath(string fileName)
        => Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "debug-screenshots", fileName));

    /** 字符屏 → 工程自身字形图集/GL 管线 → PNG(与 GpuRenderer.SaveScreenshot 同样的读回+翻转)。 */
    public static void SavePng(char[,] screen, string fileName)
    {
        var width = screen.GetLength(0);
        var height = screen.GetLength(1);
        var grid = BuildGrid(screen);

        var atlas = GlyphAtlas.Shared;
        atlas.Prewarm();
        var pixelWidth = width * atlas.GlyphWidth;
        var pixelHeight = height * atlas.GlyphHeight;
        using var egl = HeadlessGl.Create(pixelWidth, pixelHeight);
        using var core = new GpuRenderCore();
        core.Initialize(atlas);
        GL.Viewport(0, 0, pixelWidth, pixelHeight);
        var packed = new uint[width * height];
        CellPacker.PackRows(grid, 0, height, packed, atlas);
        core.EnsureCellCapacity(packed.Length);
        core.UploadCells(packed, 0, packed.Length);
        core.RenderFrame(atlas, width, height);
        GL.Finish();

        var pixels = new byte[pixelWidth * pixelHeight * 4];
        GL.ReadPixels(0, 0, pixelWidth, pixelHeight, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
        using var image = new Image<Rgba32>(pixelWidth, pixelHeight);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var sourceY = accessor.Height - 1 - y;
                for (var x = 0; x < accessor.Width; x++)
                {
                    var source = ((sourceY * accessor.Width) + x) * 4;
                    row[x] = new Rgba32(pixels[source], pixels[source + 1], pixels[source + 2], pixels[source + 3]);
                }
            }
        });

        var path = ArtifactPath(fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        image.SaveAsPng(path);
    }

    /**
     * 字符屏 → CellGrid: 逐格搬运, 保留宽字符的 '\0' 续格, 不做任何重排版。
     * 若按行文本重排版(续格被换成空格), CellText.Draw 会为每个 CJK 多前进一格, 整行尾部(含右边框)右移。
     */
    public static CellGrid BuildGrid(char[,] screen)
    {
        var grid = new CellGrid(screen.GetLength(0), screen.GetLength(1));
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
                grid[x, y] = new Cell(screen[x, y]);
        }

        return grid;
    }
}