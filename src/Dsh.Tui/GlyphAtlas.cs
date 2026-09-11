using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Drawing.Text;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tui;

public readonly record struct GlyphUv(float MinX, float MinY, float MaxX, float MaxY);

public sealed class GlyphAtlas
{
    public const int GlyphWidth = 16;
    public const int GlyphHeight = 20;
    public const int Columns = 128;
    public const char FirstCharacter = ' ';
    public const char LastCharacter = '\u9fff';

    private const float FontSize = 15f;

    private static readonly IReadOnlyList<char> Characters = BuildCharacters();
    public static readonly int Rows = (Characters.Count + Columns - 1) / Columns;
    private static readonly Lazy<GlyphAtlas> Shared = new(() => new GlyphAtlas(initialize: true));

    private readonly Dictionary<char, int> _indexes;
    private readonly GlyphUv[] _uvs;
    private readonly byte[] _textureData;

    public GlyphAtlas()
        : this(initialize: false)
    {
    }

    private GlyphAtlas(bool initialize)
    {
        if (initialize)
        {
            _indexes = BuildIndexes();
            _uvs = BuildUvs();
            _textureData = Bake(ResolveFont());
            return;
        }

        var shared = Shared.Value;
        _indexes = shared._indexes;
        _uvs = shared._uvs;
        _textureData = shared._textureData;
    }

    public int AtlasWidth => Columns * GlyphWidth;

    public int AtlasHeight => Rows * GlyphHeight;

    public byte[] CreateTextureData() => (byte[])_textureData.Clone();

    public int GetGlyphIndex(char character)
        => _indexes.TryGetValue(character, out var index) ? index : GetGlyphIndex('?');

    public GlyphUv GetUv(char character)
    {
        var index = GetGlyphIndex(character);
        var column = index % Columns;
        var row = index / Columns;
        return new GlyphUv(
            column / (float)Columns,
            row / (float)Rows,
            (column + 1) / (float)Columns,
            (row + 1) / (float)Rows);
    }

    public bool IsPixelSet(char character, int x, int y)
    {
        if ((uint)x >= GlyphWidth || (uint)y >= GlyphHeight)
            throw new ArgumentOutOfRangeException(nameof(x));
        var index = GetGlyphIndex(character);
        return _textureData[((index / Columns) * GlyphHeight + y) * AtlasWidth + ((index % Columns) * GlyphWidth + x)] != 0;
    }

    private static Dictionary<char, int> BuildIndexes()
    {
        var indexes = new Dictionary<char, int>(Characters.Count);
        for (var index = 0; index < Characters.Count; index++)
            indexes[Characters[index]] = index;
        return indexes;
    }

    private static GlyphUv[] BuildUvs()
    {
        var uvs = new GlyphUv[Characters.Count];
        for (var index = 0; index < Characters.Count; index++)
        {
            var column = index % Columns;
            var row = index / Columns;
            uvs[index] = new GlyphUv(
                column / (float)Columns,
                row / (float)Rows,
                (column + 1) / (float)Columns,
                (row + 1) / (float)Rows);
        }

        return uvs;
    }

    private byte[] Bake(Font font)
    {
        using var atlas = new Image<Rgba32>(AtlasWidth, AtlasHeight);
        using (var canvas = atlas.Frames.RootFrame.CreateCanvas(Configuration.Default, new DrawingOptions()))
        {
            var options = new TextOptions(font)
            {
                FallbackFontFamilies = ResolveFallbackFamilies(),
            };
            var offsetY = MeasureVerticalOffset(options);
            for (var index = 0; index < Characters.Count; index++)
            {
                var column = index % Columns;
                var row = index / Columns;
                options.Origin = new PointF(column * GlyphWidth, row * GlyphHeight + offsetY);
                var glyphs = TextBuilder.GenerateGlyphs(Characters[index].ToString(), options);
                foreach (var glyph in glyphs)
                    canvas.Fill(Brushes.Solid(Color.White), glyph.Paths);
            }
        }

        var texture = new byte[AtlasWidth * AtlasHeight];
        atlas.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var rowSpan = accessor.GetRowSpan(y);
                for (var x = 0; x < accessor.Width; x++)
                    texture[(y * AtlasWidth) + x] = rowSpan[x].A;
            }
        });
        return texture;
    }

    private static float MeasureVerticalOffset(TextOptions options)
    {
        var probe = TextBuilder.GenerateGlyphs("Hg中", options);
        if (probe.Count == 0)
            return 0f;
        var top = probe.Min(glyph => glyph.Bounds.Y);
        var bottom = probe.Max(glyph => glyph.Bounds.Y + glyph.Bounds.Height);
        return ((GlyphHeight - (bottom - top)) / 2f) - top;
    }

    private static IReadOnlyList<FontFamily> ResolveFallbackFamilies()
    {
        string[] names =
        [
            "Microsoft YaHei",
            "DengXian",
            "SimSun",
            "MS Gothic",
            "PingFang SC",
            "Hiragino Sans GB",
            "Noto Sans Mono CJK SC",
            "Noto Sans Mono CJK TC",
            "Noto Sans CJK SC",
            "Noto Sans CJK TC",
            "Noto Sans CJK JP",
            "WenQuanYi Micro Hei",
            "Segoe UI Symbol",
            "Segoe UI Emoji",
            "Apple Color Emoji",
            "Noto Color Emoji",
        ];

        var families = new List<FontFamily>();
        foreach (var name in names)
        {
            if (SystemFonts.TryGet(name, out var family))
                families.Add(family);
        }

        return families;
    }

    private static Font ResolveFont()
    {
        string[] preferredNames =
        [
            "Cascadia Mono",
            "Cascadia Code",
            "JetBrains Mono",
            "Consolas",
            "Menlo",
            "DejaVu Sans Mono",
            "Liberation Mono",
            "Noto Sans Mono CJK SC",
            "Noto Sans Mono CJK TC",
            "Noto Sans CJK SC",
            "Noto Sans CJK TC",
            "Microsoft YaHei",
        ];

        foreach (var name in preferredNames)
        {
            if (SystemFonts.TryGet(name, out var family))
                return family.CreateFont(FontSize);
        }

        foreach (var family in SystemFonts.Families)
        {
            if (family.Name.Contains("Mono", StringComparison.OrdinalIgnoreCase) ||
                family.Name.Contains("Console", StringComparison.OrdinalIgnoreCase) ||
                family.Name.Contains("CJK", StringComparison.OrdinalIgnoreCase))
            {
                return family.CreateFont(FontSize);
            }
        }

        var fallbackName = SystemFonts.GetDefaultFamilyName();
        return SystemFonts.CreateFont(fallbackName, FontSize);
    }

    private static IReadOnlyList<char> BuildCharacters()
    {
        var characters = new List<char>();
        for (var character = ' '; character <= '~'; character++)
            characters.Add(character);
        for (var character = '\u00a0'; character <= '\u00ff'; character++)
            characters.Add(character);
        for (var character = '\u3000'; character <= '\u303f'; character++)
            characters.Add(character);
        for (var character = '\u2000'; character <= '\u206f'; character++)
            characters.Add(character);
        for (var character = '\u2190'; character <= '\u21ff'; character++)
            characters.Add(character);
        for (var character = '\u2500'; character <= '\u259f'; character++)
            characters.Add(character);
        for (var character = '\u25a0'; character <= '\u25ff'; character++)
            characters.Add(character);
        for (var character = '\u2600'; character <= '\u27bf'; character++)
            characters.Add(character);
        for (var character = '\u2b00'; character <= '\u2bff'; character++)
            characters.Add(character);
        for (var character = '\uff00'; character <= '\uffef'; character++)
            characters.Add(character);
        for (var character = '\u4e00'; character <= '\u9fff'; character++)
            characters.Add(character);
        return characters;
    }
}