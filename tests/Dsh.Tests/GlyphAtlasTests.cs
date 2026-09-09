using Dsh.Tui;

namespace Dsh.Tests;

public class GlyphAtlasTests
{
    [Fact]
    public void GetUv_ForFirstGlyph_ReturnsTopLeftCell()
    {
        var atlas = new GlyphAtlas();

        var uv = atlas.GetUv(' ');

        Assert.Equal(0f, uv.MinX);
        Assert.Equal(0f, uv.MinY);
        Assert.Equal(1f / GlyphAtlas.Columns, uv.MaxX);
        Assert.Equal(1f / GlyphAtlas.Rows, uv.MaxY);
    }

    [Fact]
    public void GetUv_ForUnknownCharacter_FallsBackToQuestionMark()
    {
        var atlas = new GlyphAtlas();

        Assert.Equal(atlas.GetUv('?'), atlas.GetUv('\ue000'));
    }

    [Fact]
    public void GetUv_ForA_UsesAtlasRowFromIndex()
    {
        var atlas = new GlyphAtlas();
        var index = atlas.GetGlyphIndex('A');
        var row = index / GlyphAtlas.Columns;

        Assert.Equal('A' - GlyphAtlas.FirstCharacter, index);

        var uv = atlas.GetUv('A');
        Assert.Equal(row / (float)GlyphAtlas.Rows, uv.MinY);
        Assert.Equal((row + 1) / (float)GlyphAtlas.Rows, uv.MaxY);
    }

    [Fact]
    public void IsPixelSet_A_HasVisiblePixels()
    {
        var atlas = new GlyphAtlas();
        var setPixels = 0;

        for (var y = 0; y < GlyphAtlas.GlyphHeight; y++)
        {
            for (var x = 0; x < GlyphAtlas.GlyphWidth; x++)
            {
                if (atlas.IsPixelSet('A', x, y))
                    setPixels++;
            }
        }

        Assert.True(setPixels > 0);
    }

    [Fact]
    public void IsPixelSet_CjkCharacter_HasVisiblePixels()
    {
        var atlas = new GlyphAtlas();
        var setPixels = 0;

        for (var y = 0; y < GlyphAtlas.GlyphHeight; y++)
        {
            for (var x = 0; x < GlyphAtlas.GlyphWidth; x++)
            {
                if (atlas.IsPixelSet('中', x, y))
                    setPixels++;
            }
        }

        Assert.True(setPixels > 0);
    }

    [Fact]
    public void CreateTextureData_ReturnsAtlasSizedCopy()
    {
        var atlas = new GlyphAtlas();

        var data = atlas.CreateTextureData();

        Assert.Equal(
            GlyphAtlas.Columns * GlyphAtlas.Rows * GlyphAtlas.GlyphWidth * GlyphAtlas.GlyphHeight,
            data.Length);
    }
}
