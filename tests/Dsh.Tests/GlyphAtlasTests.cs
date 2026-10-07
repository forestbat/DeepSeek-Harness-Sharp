using Dsh.Tui;

namespace Dsh.Tests;

/** 需要真实字体渲染环境(CI 无 CJK 字体时烘不出字形): 只在具备真实桌面/字体的机器上跑。 */
[Trait("Category", "OnlyGpu")]
public class GlyphAtlasTests
{
    [Fact]
    public void GetUv_ForFirstGlyph_ReturnsTopLeftCell()
    {
        var atlas = CreateIsolated();

        var uv = atlas.GetUv(' ');

        Assert.Equal(0f, uv.MinX);
        Assert.Equal(0f, uv.MinY);
        Assert.Equal(1f / GlyphAtlas.Columns, uv.MaxX);
        Assert.Equal(1f / GlyphAtlas.Rows, uv.MaxY);
    }

    [Fact]
    public void GetUv_ForUnknownCharacter_FallsBackToQuestionMark()
    {
        var atlas = CreateIsolated();

        Assert.Equal(atlas.GetUv('?'), atlas.GetUv('\uE000'));
    }

    [Fact]
    public void GetUv_ForA_UsesAtlasRowFromIndex()
    {
        var atlas = CreateIsolated();
        var slot = atlas.GetGlyphIndex('A');
        var row = slot / GlyphAtlas.Columns;

        var uv = atlas.GetUv('A');
        Assert.Equal(row / (float)GlyphAtlas.Rows, uv.MinY);
        Assert.Equal((row + 1) / (float)GlyphAtlas.Rows, uv.MaxY);
    }

    [Fact]
    public void Cache_RoundTrip_RestoresSlotsAndPixels()
    {
        var path = IsolatedCachePath();
        var atlas = new GlyphAtlas(path);
        var slot = atlas.GetGlyphIndex('A');
        var uv = atlas.GetUv('A');
        atlas.SaveCacheIfDirty();
        Assert.True(File.Exists(path));

        var restored = new GlyphAtlas(path);

        Assert.Equal(slot, restored.GetGlyphIndex('A'));
        Assert.Equal(uv, restored.GetUv('A'));
        for (var y = 0; y < atlas.GlyphHeight; y++)
        {
            for (var x = 0; x < atlas.GlyphWidth; x++)
                Assert.Equal(atlas.IsPixelSet('A', x, y), restored.IsPixelSet('A', x, y));
        }
    }

    [Theory]
    [InlineData('A')]
    [InlineData('中')]
    [InlineData('─')]
    [InlineData('…')]
    [InlineData('›')]
    public void IsPixelSet_SupportedCharacters_HaveVisiblePixels(char character)
    {
        var atlas = CreateIsolated();
        var setPixels = 0;

        for (var y = 0; y < atlas.GlyphHeight; y++)
        {
            for (var x = 0; x < atlas.GlyphWidth; x++)
            {
                if (atlas.IsPixelSet(character, x, y))
                    setPixels++;
            }
        }

        Assert.True(setPixels > 0);
    }

    [Fact]
    public void IsPixelSet_LatinAndCjk_ShareBaseline()
    {
        var atlas = CreateIsolated();
        var latinBottom = LastSetRow(atlas, 'A');
        var cjkBottom = LastSetRow(atlas, '中');

        // 度量只取主字体、回退字体字形按同一基线绘制; 回退字体自身墨水底边可差 1~3px(YaHei UI/Noto CJK 实测),
        // 故这里断言"大致共享基线且不出格": 差值 ≤4px, 且 CJK 墨水底边必须在格内(不被裁)。
        Assert.InRange(Math.Abs(latinBottom - cjkBottom), 0, 4);
        Assert.True(cjkBottom < atlas.GlyphHeight, $"CJK 墨水底边 {cjkBottom} 超出行高 {atlas.GlyphHeight}");
    }

    [Fact]
    public void Wide_Glyph_Bakes_Into_Second_Slot_Without_Clipping()
    {
        var atlas = CreateIsolated();
        var rightHalfInk = 0;
        for (var y = 0; y < atlas.GlyphHeight; y++)
        {
            for (var x = atlas.GlyphWidth; x < atlas.GlyphWidth * 2; x++)
            {
                if (atlas.IsPixelSet('中', x, y))
                    rightHalfInk++;
            }
        }

        Assert.True(rightHalfInk > 0, "宽字形应烘进第二个槽(自然尺寸, 不被单格宽裁剪)");
    }

    private static int LastSetRow(GlyphAtlas atlas, char character)
    {
        for (var y = atlas.GlyphHeight - 1; y >= 0; y--)
        {
            for (var x = 0; x < atlas.GlyphWidth; x++)
            {
                if (atlas.IsPixelSet(character, x, y))
                    return y;
            }
        }

        return -1;
    }

    [Fact]
    public void CreateTextureData_ReturnsAtlasSizedCopy()
    {
        var atlas = CreateIsolated();

        var data = atlas.CreateTextureData();

        Assert.Equal(
            GlyphAtlas.Columns * GlyphAtlas.Rows * atlas.GlyphWidth * atlas.GlyphHeight,
            data.Length);
    }

    private static GlyphAtlas CreateIsolated() => new(IsolatedCachePath());

    private static string IsolatedCachePath()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-tmp"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"glyph-cache-{Guid.NewGuid():N}.bin");
    }
}
