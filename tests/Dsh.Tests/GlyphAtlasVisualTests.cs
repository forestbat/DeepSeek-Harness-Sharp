using Dsh.Tui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tests;

public class GlyphAtlasVisualTests
{
    [Fact]
    public void WriteGlyphSampleToProjectArtifacts()
    {
        var atlas = new GlyphAtlas();
        const string characters = "ABCabc012中文字体─│┌┐…›↑⚙✓";
        const int scale = 8;
        var image = new Image<Rgba32>(characters.Length * GlyphAtlas.GlyphWidth * scale, GlyphAtlas.GlyphHeight * scale);

        for (var characterIndex = 0; characterIndex < characters.Length; characterIndex++)
        {
            for (var y = 0; y < GlyphAtlas.GlyphHeight; y++)
            {
                for (var x = 0; x < GlyphAtlas.GlyphWidth; x++)
                {
                    if (!atlas.IsPixelSet(characters[characterIndex], x, y))
                        continue;
                    for (var dy = 0; dy < scale; dy++)
                    {
                        for (var dx = 0; dx < scale; dx++)
                        {
                            image[((x + (characterIndex * GlyphAtlas.GlyphWidth)) * scale) + dx, (y * scale) + dy] =
                                new Rgba32(255, 255, 255, 255);
                        }
                    }
                }
            }
        }

        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/gpu-screenshots"));
        Directory.CreateDirectory(directory);
        image.SaveAsPng(Path.Combine(directory, "glyph-sample.png"));
    }
}