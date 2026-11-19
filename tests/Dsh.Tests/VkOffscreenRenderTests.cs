using Dsh.Tui;
using Dsh.Tui.Vk;

namespace Dsh.Tests;

/**
 * Vulkan 离屏文本渲染器回归用例: 固化 plans/DSH-Sharp-§4-Vulkan与cellgrid方案.md §9 的硬性不变量。
 *  - 同输入两帧逐字节一致(恒全帧重绘, 确定性);
 *  - 宽字符续格('\0')不画字形(≠空格也无差别);
 *  - 上一帧的字形必须被下一帧完整清除(双缓冲脏行重放回归, §9 B4);
 *  - 显式背景色真实落像素。
 * 无 Vulkan 设备时动态跳过(CI 标准 runner 无 GPU)。
 */
[Trait("Category", "OnlyGpu")]
public sealed class VkOffscreenRenderTests
{
    [Fact]
    public void SameGrid_TwoFrames_IsPixelIdentical()
    {
        using var fixture = Fixture.Create();
        var grid = fixture.NewGrid();
        grid[0, 0] = new Cell('A');
        grid[1, 0] = new Cell('新');
        grid[2, 0] = new Cell('\0');

        var first = fixture.Render(grid);
        var second = fixture.Render(grid);

        Assert.Equal(first, second);
    }

    [Fact]
    public void WideChar_ContinuationCell_DrawsNoGlyph()
    {
        using var fixture = Fixture.Create();

        // 用 SetRow 直接写行: 绕过 CellGrid 的 RepairWideCharSplit(否则给续格位置写空格会按契约清掉左侧悬空宽字符)。
        var withContinuation = fixture.NewGrid();
        withContinuation.SetRow(0, [new Cell('新'), new Cell('\0')]);
        var a = fixture.Render(withContinuation);

        var withoutContinuation = fixture.NewGrid();
        withoutContinuation.SetRow(0, [new Cell('新'), new Cell(' ')]);
        var b = fixture.Render(withoutContinuation);

        // 续格('\0')与空格都不画字形(§9 B1): 两帧必须逐字节一致。
        Assert.True(a.AsSpan().SequenceEqual(b), fixture.DescribeDiff(a, b));
    }

    [Fact]
    public void Glyph_RemovedOnNextFrame_LeavesNoStaleInk()
    {
        using var fixture = Fixture.Create();

        var withGlyph = fixture.NewGrid();
        withGlyph[2, 1] = new Cell('A');
        var inked = fixture.Render(withGlyph);
        Assert.True(inked.Any(component => component > 200), "先画一个字形: 应出现接近白色的墨点");

        var blank = fixture.NewGrid();
        var cleared = fixture.Render(blank);

        // 初始/默认背景为纯黑; 上一帧的字形若靠"脏行重放"就会残留(历史 bug)。只校验 RGB(A 被背景趟写成不透明)。
        for (var index = 0; index < cleared.Length; index += 4)
        {
            Assert.Equal(0, cleared[index]);
            Assert.Equal(0, cleared[index + 1]);
            Assert.Equal(0, cleared[index + 2]);
        }
    }

    [Fact]
    public void ExplicitBackgroundColor_IsRendered()
    {
        using var fixture = Fixture.Create();
        var grid = fixture.NewGrid();
        grid[0, 0] = new Cell(' ', default, CellColor.FromRgb(10, 200, 30));

        var pixels = fixture.Render(grid);
        var (r, g, b) = fixture.SampleCenter(pixels, 0, 0);

        Assert.Equal(10, r);
        Assert.Equal(200, g);
        Assert.Equal(30, b);

        // 整格必须铺满该背景色。每格用 6 顶点拼方块时, 若第二个三角形不与第一个共用对角线,
        // 格子下半会留一个三角空洞(显示为清屏色), 这里逐像素就能暴露出来。
        for (var y = 0; y < fixture.GlyphHeight; y++)
        {
            for (var x = 0; x < fixture.GlyphWidth; x++)
            {
                var index = ((y * fixture.Width) + x) * 4;
                Assert.Equal(10, pixels[index]);
                Assert.Equal(200, pixels[index + 1]);
                Assert.Equal(30, pixels[index + 2]);
            }
        }
    }

    /**
     * 字形必须"按图集格子原样"落像素: 渲染出的墨迹竖向跨度应等于图集槽位内的墨迹跨度。
     * 若渲染跨度明显更小(Vk 侧采样/上传把格子压扁), 用户看到的就是"字符下半被黑边盖住"。
     */
    [Fact]
    public void GlyphInk_VerticalSpan_MatchesAtlasSlot()
    {
        using var fixture = Fixture.Create();
        var grid = fixture.NewGrid(2, 1);
        grid.SetRow(0, [new Cell('A'), new Cell(' ')]);
        var pixels = fixture.Render(grid);

        var rendered = fixture.InkSpan(pixels, 0, 0);
        var atlas = fixture.AtlasInkSpan('A');
        Assert.True(atlas.Length > 1, $"图集里 'A' 应有墨迹(实测跨度 {atlas.Length})");
        Assert.True(
            Math.Abs(rendered.Length - atlas.Length) <= 1,
            $"渲染墨迹竖向跨度 {rendered.Length} ≠ 图集槽位 {atlas.Length} (格高 {fixture.GlyphHeight}; 渲染 y={rendered.First}..{rendered.Last}, 图集 y={atlas.First}..{atlas.Last})");

        // 第二帧引入新字形 'B': 第一帧走整图上传, 之后的新字形走"脏槽增量上传", 两条路径必须给出同样的墨迹。
        var second = fixture.NewGrid(2, 1);
        second.SetRow(0, [new Cell('B'), new Cell(' ')]);
        var pixels2 = fixture.Render(second);
        var renderedB = fixture.InkSpan(pixels2, 0, 0);
        var atlasB = fixture.AtlasInkSpan('B');
        Assert.True(atlasB.Length > 1, $"图集里 'B' 应有墨迹(实测跨度 {atlasB.Length})");
        Assert.True(
            Math.Abs(renderedB.Length - atlasB.Length) <= 1,
            $"脏槽上传后渲染跨度 {renderedB.Length} ≠ 图集 {atlasB.Length} (渲染 y={renderedB.First}..{renderedB.Last}, 图集 y={atlasB.First}..{atlasB.Last})");

        // 带降部(descender)的字形: 墨迹必须落到格子下部, 且与图集槽位逐行一致。
        // (用户可见: "字体下方都被遮住/剪切" —— 'A' 这类无降部字形下缘正好落在基线, 所以看不出。)
        // 回归目标: Prepare(写 staging + 提交 atlas 拷贝) 必须等在途帧结束, 否则新旧数据在共享 atlas 纹理里混合, 下缘被混掉。
        var descender = fixture.NewGrid(2, 1);
        descender.SetRow(0, [new Cell('g'), new Cell(' ')]);
        var descenderPixels = fixture.Render(descender);
        var descenderRendered = fixture.InkSpan(descenderPixels, 0, 0);
        var descenderAtlas = fixture.AtlasInkSpan('g');
        Assert.True(
            descenderAtlas.Last >= fixture.GlyphHeight - 3,
            $"图集里 'g' 的降部只到 y={descenderAtlas.Last}(格高 {fixture.GlyphHeight}), 下缘被画布裁掉");

        // 屏幕墨迹跨度必须与图集槽位一致: 每格用 6 顶点拼方块时, 第二个三角形若不与第一个共用
        // 对角线 (1,0)-(0,1), 两个三角形就会在上半部重叠、下半部留一个三角空洞, 带降部字形
        // (以及光标/背景色块)的下缘被挖掉, 正是用户看到的"字符下半被黑边盖住"。
        Assert.True(
            Math.Abs(descenderRendered.Length - descenderAtlas.Length) <= 1,
            $"渲染 'g' 墨迹跨度 {descenderRendered.Length} ≠ 图集 {descenderAtlas.Length} (渲染 y={descenderRendered.First}..{descenderRendered.Last}, 图集 y={descenderAtlas.First}..{descenderAtlas.Last})");
    }

    /**
     * GPU 上的字形图集与字形映射表必须与 CPU 侧完全一致(上传路径正确性的回归)。
     */
    [Fact]
    public void GpuAtlasAndGlyphMap_MatchCpu()
    {
        using var fixture = Fixture.Create();
        var grid = fixture.NewGrid(2, 1);
        grid.SetRow(0, [new Cell('A'), new Cell('g')]);
        fixture.Render(grid);

        var gpuAtlas = new byte[fixture.AtlasBytes];
        fixture.Core.ReadAtlasTexture(gpuAtlas);
        var cpuAtlas = fixture.CpuAtlas;
        Assert.Equal(cpuAtlas.Length, gpuAtlas.Length);
        for (var index = 0; index < cpuAtlas.Length; index++)
            Assert.Equal(cpuAtlas[index], gpuAtlas[index]);

        var gpuMap = new int[256 * 256];
        fixture.Core.ReadGlyphMapTexture(gpuMap);
        Assert.Equal(fixture.CpuMap('A'), gpuMap['A']);
        Assert.Equal(fixture.CpuMap('g'), gpuMap['g']);
    }

    /** 一段墨迹的竖向范围(含首末行)与跨度。 */
    private readonly record struct InkBand(int First, int Last)
    {
        public int Length => Last < 0 ? 0 : Last - First + 1;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GlyphAtlas _atlas;
        private readonly VkOffscreenRenderer _renderer;

        private Fixture(GlyphAtlas atlas, VkOffscreenRenderer renderer)
        {
            _atlas = atlas;
            _renderer = renderer;
        }

        public static Fixture Create()
        {
            GlyphAtlas atlas;
            try
            {
                atlas = new GlyphAtlas(IsolatedCachePath());
            }
            catch (Exception ex)
            {
                Assert.Skip($"无法构建字形图集: {ex.Message}");
                return null!;
            }

            VkOffscreenRenderer renderer;
            try
            {
                renderer = VkOffscreenRenderer.Create(atlas);
            }
            catch (Exception ex)
            {
                Assert.Skip($"无可用 Vulkan 设备: {ex.Message}");
                return null!;
            }

            return new Fixture(atlas, renderer);
        }

        public CellGrid NewGrid(int width = 8, int height = 4) => new(width, height);

        public byte[] Render(CellGrid grid)
        {
            _renderer.Render(grid);
            var pixels = new byte[_renderer.Width * _renderer.Height * 4];
            _renderer.ReadPixels(pixels);
            return pixels;
        }

        public (byte R, byte G, byte B) SampleCenter(byte[] pixels, int cellX, int cellY)
        {
            var x = (cellX * _atlas.GlyphWidth) + (_atlas.GlyphWidth / 2);
            var y = (cellY * _atlas.GlyphHeight) + (_atlas.GlyphHeight / 2);
            var index = ((y * _renderer.Width) + x) * 4;
            return (pixels[index], pixels[index + 1], pixels[index + 2]);
        }

        public void Dispose() => _renderer.Dispose();

        public int GlyphHeight => _atlas.GlyphHeight;

        public int GlyphWidth => _atlas.GlyphWidth;

        public int Width => _renderer.Width;

        /** CPU 侧图集位图(与 GPU 上传源一致)。 */
        public byte[] CpuAtlas => _atlas.TextureData;

        public VkRenderCore Core => _renderer.Core;

        public int CpuMap(char character) => _atlas.MapUpload[character];

        public int AtlasBytes => _atlas.AtlasWidth * _atlas.AtlasHeight;

        public InkBand InkSpan(byte[] pixels, int cellX, int cellY, int spanWidth = 0)
        {
            if (spanWidth <= 0)
                spanWidth = _atlas.GlyphWidth;
            var first = int.MaxValue;
            var last = -1;
            for (var y = 0; y < _atlas.GlyphHeight; y++)
            {
                for (var x = 0; x < spanWidth; x++)
                {
                    var index = ((((cellY * _atlas.GlyphHeight) + y) * _renderer.Width) + (cellX * _atlas.GlyphWidth) + x) * 4;
                    if (pixels[index] <= 128)
                        continue;
                    first = Math.Min(first, y);
                    last = Math.Max(last, y);
                }
            }

            return last < 0 ? new InkBand(-1, -1) : new InkBand(first, last);
        }

        /** 图集槽位内同一字形的墨迹竖向范围(自上而下, 与 Bake 写 _textureData 的行序一致)。 */
        public InkBand AtlasInkSpan(char character, int spanWidth = 0)
        {
            if (spanWidth <= 0)
                spanWidth = _atlas.GlyphWidth;
            var slot = (_atlas.MapUpload[character] & 0x7FFFFFFF) - 1;
            if (slot < 0)
                return new InkBand(-1, -1);
            var slotX = (slot % GlyphAtlas.Columns) * _atlas.GlyphWidth;
            var slotY = (slot / GlyphAtlas.Columns) * _atlas.GlyphHeight;
            var data = _atlas.TextureData;
            var first = int.MaxValue;
            var last = -1;
            for (var y = 0; y < _atlas.GlyphHeight; y++)
            {
                for (var x = 0; x < spanWidth; x++)
                {
                    if (data[((slotY + y) * _atlas.AtlasWidth) + slotX + x] <= 128)
                        continue;
                    first = Math.Min(first, y);
                    last = Math.Max(last, y);
                }
            }

            return last < 0 ? new InkBand(-1, -1) : new InkBand(first, last);
        }

        public string DescribeDiff(byte[] a, byte[] b)
        {
            var cells = new Dictionary<(int X, int Y), int>();
            for (var pixel = 0; pixel < a.Length / 4; pixel++)
            {
                var i = pixel * 4;
                if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3])
                    continue;
                var key = (pixel % _renderer.Width / _atlas.GlyphWidth, pixel / _renderer.Width / _atlas.GlyphHeight);
                cells[key] = cells.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            var centerA = SampleCenter(a, 0, 0);
            var centerB = SampleCenter(b, 0, 0);
            var contA = SampleCenter(a, 1, 0);
            var contB = SampleCenter(b, 1, 0);
            var histogram = string.Join(", ", cells.OrderBy(p => p.Key.Y).ThenBy(p => p.Key.X).Select(p => $"({p.Key.X},{p.Key.Y})={p.Value}px"));
            return $"差异格: {histogram}; cell(0,0) a={centerA} b={centerB}; cell(1,0) a={contA} b={contB}";
        }

        private static string IsolatedCachePath()
        {
            var directory = Path.Combine(TempTree.RepositoryRoot, "tests/.scratch/test-tmp");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $"vk-glyph-cache-{Guid.NewGuid():N}.bin");
        }
    }
}
