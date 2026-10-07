using Dsh.Tui;

namespace Dsh.Tests;

/** 回退族解析缓存: 命中/失效/损坏都要安全(P2)。 */
public sealed class GlyphFallbackCacheTests
{
    private static readonly string Root = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts020/test-homes"));

    [Fact]
    public void RoundTrip_And_Key_Invalidation()
    {
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "glyph-fallback.txt");
        var key = GlyphFallbackCache.Key(17.333, ["Cascadia Mono", "Noto Sans SC"]);
        try
        {
            Assert.Null(GlyphFallbackCache.TryLoad(path, key));

            GlyphFallbackCache.Save(path, key, ["Noto Sans SC", "Noto Sans CJK SC"]);
            Assert.Equal(["Noto Sans SC", "Noto Sans CJK SC"], GlyphFallbackCache.TryLoad(path, key));

            Assert.Null(GlyphFallbackCache.TryLoad(path, "另一个字体清单的键"));
            Assert.NotEqual(key, GlyphFallbackCache.Key(17.333, ["Cascadia Mono"]));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Broken_File_Is_Treated_As_Miss()
    {
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "glyph-fallback.txt");
        var key = GlyphFallbackCache.Key(17.333, ["Cascadia Mono"]);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, "只有一行");

            Assert.Null(GlyphFallbackCache.TryLoad(path, key));
            Assert.Null(GlyphFallbackCache.TryLoad(Path.Combine(directory, "缺失.txt"), key));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
        }
    }
}
