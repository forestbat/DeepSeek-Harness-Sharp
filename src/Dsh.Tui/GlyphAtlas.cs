using Dsh.Boot;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Drawing.Text;
using SixLabors.ImageSharp.PixelFormats;

namespace Dsh.Tui;

public readonly record struct GlyphUv(float MinX, float MinY, float MaxX, float MaxY);

/**
 * 字形图集: 度量照抄 Alacritty/crossfont —— 格宽 = floor(advance('0')),
 * 格高 = floor(max(字体声明行高, ascent − descent)), 基线距格顶 = 格高 + descent(负值);
 * 宽字形占同一行的两个连续槽(位图按自然尺寸烘焙, 绝不缩放), 窄字形占一槽。
 */
public sealed class GlyphAtlas
{
    public const int Columns = 128;
    public const int Rows = 64;
    public const int Capacity = Columns * Rows;

    /** 默认字号(磅): 即 Rider/JetBrains IDE 终端的默认字号; 格尺寸由字体度量推导, 不硬编码。 */
    public const double DefaultFontSizePt = 13;

    private const string CacheMagic = "DSHGLYF3";

    /** 字号(磅)→ 六线字号(px, Dpi=72 下 1pt=1px)。 */
    private const float DefaultFontSize = 17.333f;

    private static readonly Lazy<GlyphAtlas> SharedInstance = new(() => new GlyphAtlas());
    private static readonly Lazy<FontFamily> SharedFamily = new(ResolveFontFamily);
    private static readonly object FallbackGate = new();
    /** 回退族懒解析(P3): 纯拉丁/框线文本不触发; 结果按字体清单指纹缓存(P2)。 */
    private static readonly Lazy<IReadOnlyList<FontFamily>> FallbackFamiliesLazy = new(ResolveFallbackFamilies);
    private static IReadOnlyList<FontFamily>? _fallbackOverride;

    public static GlyphAtlas Shared => SharedInstance.Value;

    private readonly object _gate = new();
    private readonly int[] _map = CreateEmptyMap();
    private readonly int[] _mapUpload = new int[char.MaxValue + 1];
    private readonly byte[] _bakedBitmap = new byte[(char.MaxValue + 1) / 8];
    private readonly char[] _slotChars = new char[Capacity];
    private readonly bool[] _slotPairTail = new bool[Capacity];
    private readonly long[] _slotTicks = new long[Capacity];
    private readonly GlyphUv[] _uvs = BuildUvs();
    private readonly byte[] _textureData;
    private readonly List<int> _dirtySlots = [];
    private readonly string _cachePath;
    private readonly float _fontSize;
    private readonly Font _font;
    private readonly float _originY;
    private int _nextSlot;
    private bool _cacheDirty;
    private long _accessCounter;

    /** 字符格像素宽(字体 advance('0') 向下取整)。 */
    public int GlyphWidth { get; }

    /** 字符格像素高(max(声明行高, ascent − descent) 向下取整)。 */
    public int GlyphHeight { get; }

    public int MapVersion { get; private set; }

    internal ReadOnlySpan<int> MapUpload => _mapUpload;

    internal byte[] BakedBitmap => _bakedBitmap;

    public void EnsureBaked(ReadOnlySpan<Cell> cells)
    {
        List<char>? missing = null;
        ref var bitmapRef = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(_bakedBitmap);
        foreach (ref readonly var cell in cells)
        {
            var character = cell.Character;
            if (character is '\0' or ' ' || (System.Runtime.CompilerServices.Unsafe.Add(ref bitmapRef, character >> 3) & (1 << (character & 7))) != 0)
                continue;
            missing ??= [];
            if (!missing.Contains(character))
                missing.Add(character);
        }
        if (missing is null)
            return;
        foreach (var character in missing)
            BakeSlow(character);
    }

    /** fontSizePt 为字号(磅, 默认 13pt); 缓存按格尺寸分文件, 互不污染。 */
    public GlyphAtlas(string? cachePath = null, double fontSizePt = DefaultFontSizePt)
    {
        if (!double.IsFinite(fontSizePt) || fontSizePt <= 0)
            fontSizePt = DefaultFontSizePt;
        var ratio = fontSizePt / DefaultFontSizePt;
        _fontSize = DefaultFontSize * (float)ratio;
        _font = SharedFamily.Value.CreateFont(_fontSize);
        var metrics = _font.FontMetrics;
        var horizontal = metrics.HorizontalMetrics;
        var pixelsPerUnit = _font.Size / metrics.UnitsPerEm;
        var descentPixels = horizontal.Descender * pixelsPerUnit;
        GlyphWidth = Math.Max(1, (int)Math.Floor(TextMeasurer.MeasureAdvance("0", CreateOptions("0")).Width));
        GlyphHeight = Math.Max(1, (int)Math.Floor(Math.Max(horizontal.LineHeight, horizontal.Ascender - horizontal.Descender) * pixelsPerUnit));
        _originY = GlyphHeight + descentPixels - MeasureLayoutBaseline();
        _textureData = new byte[Columns * GlyphWidth * Rows * GlyphHeight];
        _cachePath = cachePath ?? DefaultCachePath(fontSizePt);
        LoadCache();
    }

    public int AtlasWidth => Columns * GlyphWidth;

    public int AtlasHeight => Rows * GlyphHeight;

    internal byte[] TextureData => _textureData;

    public int DirtyCount
    {
        get
        {
            lock (_gate)
                return _dirtySlots.Count;
        }
    }

    public int GetGlyphIndex(char character)
    {
        var slot = _map[character];
        if (slot >= 0)
        {
            _slotTicks[slot] = ++_accessCounter;
            return slot;
        }
        return BakeSlow(character);
    }

    public GlyphUv GetUv(char character) => _uvs[GetGlyphIndex(character)];

    public bool IsPixelSet(char character, int x, int y)
    {
        if (ShouldFallback(character))
            character = '?';
        var maxX = TerminalTextWidth.IsWide(character) ? GlyphWidth * 2 : GlyphWidth;
        if ((uint)x >= (uint)maxX || (uint)y >= (uint)GlyphHeight)
            throw new ArgumentOutOfRangeException(nameof(x));
        var slot = GetGlyphIndex(character);
        return _textureData[((slot / Columns) * GlyphHeight + y) * AtlasWidth + ((slot % Columns) * GlyphWidth + x)] != 0;
    }

    public byte[] CreateTextureData() => (byte[])_textureData.Clone();

    public int FlushDirtyRegions(Span<int> slots)
    {
        lock (_gate)
        {
            var count = Math.Min(slots.Length, _dirtySlots.Count);
            for (var index = 0; index < count; index++)
                slots[index] = _dirtySlots[index];
            _dirtySlots.RemoveRange(0, count);
            return count;
        }
    }

    public void Prewarm()
    {
        for (var character = ' '; character <= '~'; character++)
            GetGlyphIndex(character);
        for (var character = '─'; character <= '┿'; character++)
            GetGlyphIndex(character);
        for (var character = '▀'; character <= '▟'; character++)
            GetGlyphIndex(character);
    }

    public void SaveCacheIfDirty()
    {
        lock (_gate)
        {
            if (!_cacheDirty)
                return;
            var entries = new List<(char Character, int Slot)>();
            for (var slot = 0; slot < _nextSlot; slot++)
                entries.Add((_slotChars[slot], slot));
            var directory = Path.GetDirectoryName(_cachePath);
            if (directory is not null)
                Directory.CreateDirectory(directory);
            var tempPath = $"{_cachePath}.tmp";
            using (var stream = File.Create(tempPath))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(CacheMagic);
                writer.Write(SharedFamily.Value.Name);
                writer.Write(GlyphWidth);
                writer.Write(GlyphHeight);
                writer.Write(Columns);
                writer.Write(Rows);
                writer.Write(_fontSize);
                writer.Write(entries.Count);
                foreach (var (character, slot) in entries)
                {
                    writer.Write((ushort)character);
                    writer.Write(slot);
                }
                foreach (var (_, slot) in entries)
                {
                    var slotX = (slot % Columns) * GlyphWidth;
                    var slotY = (slot / Columns) * GlyphHeight;
                    for (var y = 0; y < GlyphHeight; y++)
                        writer.Write(_textureData, ((slotY + y) * AtlasWidth) + slotX, GlyphWidth);
                }
            }
            File.Move(tempPath, _cachePath, overwrite: true);
            _cacheDirty = false;
        }
    }

    internal int BakeSlow(char character)
    {
        if (ShouldFallback(character))
            character = '?';
        lock (_gate)
        {
            var slot = _map[character];
            if (slot >= 0)
                return slot;
            var wide = TerminalTextWidth.IsWide(character);
            slot = AllocateSlot(wide);
            Bake(character, slot, wide);
            _slotChars[slot] = character;
            _slotTicks[slot] = ++_accessCounter;
            if (wide)
                _slotTicks[slot + 1] = _slotTicks[slot];
            _map[character] = slot;
            _bakedBitmap[character >> 3] |= (byte)(1 << (character & 7));
            _mapUpload[character] = (slot + 1) | (wide ? int.MinValue : 0);
            MapVersion++;
            _dirtySlots.Add(slot);
            _cacheDirty = true;
            return slot;
        }
    }

    /** 宽字形需要同一行内两个连续槽, 行尾剩单列时让到下一行; 容量不足走 LRU 驱逐。 */
    private int AllocateSlot(bool wide)
    {
        if (wide && _nextSlot % Columns == Columns - 1)
            _nextSlot++;
        if (_nextSlot + (wide ? 2 : 1) <= Capacity)
        {
            var slot = _nextSlot;
            _nextSlot += wide ? 2 : 1;
            if (wide)
                _slotPairTail[slot + 1] = true;
            return slot;
        }
        return EvictOldest(wide);
    }

    private int EvictOldest(bool wide)
    {
        while (true)
        {
            var oldest = -1;
            for (var slot = 0; slot < Capacity; slot++)
            {
                if (_slotPairTail[slot] || _slotChars[slot] == '\0')
                    continue;
                if (oldest < 0 || _slotTicks[slot] < _slotTicks[oldest])
                    oldest = slot;
            }
            if (oldest < 0)
            {
                // 理论上不可达(容量内总有头槽); 兜底复位, 保证调用方拿到可用槽
                Array.Fill(_map, -1);
                Array.Clear(_bakedBitmap);
                Array.Clear(_slotChars);
                Array.Clear(_slotPairTail);
                _nextSlot = 0;
                MapVersion++;
                return AllocateSlot(wide);
            }
            FreeSlot(oldest);
            if (!wide)
                return oldest;
            if (oldest % Columns <= Columns - 2 && IsFreeSlot(oldest + 1))
            {
                _slotPairTail[oldest + 1] = true;
                return oldest;
            }
        }
    }

    private void FreeSlot(int slot)
    {
        var character = _slotChars[slot];
        _slotChars[slot] = '\0';
        _slotPairTail[slot] = false;
        _slotTicks[slot] = 0;
        if (character == '\0')
            return;
        if (TerminalTextWidth.IsWide(character) && slot + 1 < Capacity)
        {
            _slotPairTail[slot + 1] = false;
            _slotChars[slot + 1] = '\0';
        }
        _map[character] = -1;
        _bakedBitmap[character >> 3] &= (byte)~(1 << (character & 7));
        _mapUpload[character] = 0;
        MapVersion++;
    }

    private bool IsFreeSlot(int slot)
        => _slotChars[slot] == '\0' && !_slotPairTail[slot];

    private void Bake(char character, int slot, bool wide)
    {
        var slotWidth = wide ? GlyphWidth * 2 : GlyphWidth;
        using var image = new Image<Rgba32>(slotWidth, GlyphHeight);
        using (var canvas = image.Frames.RootFrame.CreateCanvas(Configuration.Default, new DrawingOptions()))
        {
            var options = CreateOptions(character.ToString());
            options.Origin = new PointF(0, _originY);
            BakeGlyphs(canvas, character, options);
        }

        var slotX = (slot % Columns) * GlyphWidth;
        var slotY = (slot / Columns) * GlyphHeight;
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var rowSpan = accessor.GetRowSpan(y);
                for (var x = 0; x < accessor.Width; x++)
                    _textureData[((slotY + y) * AtlasWidth) + slotX + x] = rowSpan[x].A;
            }
        });
        if (wide)
            _dirtySlots.Add(slot + 1);
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return;
            using var stream = File.OpenRead(_cachePath);
            using var reader = new BinaryReader(stream);
            if (reader.ReadString() != CacheMagic)
                return;
            var fontName = reader.ReadString();
            if (fontName != SharedFamily.Value.Name
                || reader.ReadInt32() != GlyphWidth
                || reader.ReadInt32() != GlyphHeight
                || reader.ReadInt32() != Columns
                || reader.ReadInt32() != Rows
                || Math.Abs(reader.ReadSingle() - _fontSize) > 0.001f)
                return;
            var count = reader.ReadInt32();
            if (count <= 0 || count > Capacity)
                return;
            var entries = new (char Character, int Slot)[count];
            for (var index = 0; index < count; index++)
            {
                var character = (char)reader.ReadUInt16();
                var slot = reader.ReadInt32();
                if (slot < 0 || slot >= Capacity)
                    return;
                entries[index] = (character, slot);
            }
            foreach (var (character, slot) in entries)
            {
                var slotX = (slot % Columns) * GlyphWidth;
                var slotY = (slot / Columns) * GlyphHeight;
                for (var y = 0; y < GlyphHeight; y++)
                {
                    var read = reader.Read(_textureData, ((slotY + y) * AtlasWidth) + slotX, GlyphWidth);
                    if (read != GlyphWidth)
                        return;
                }
                _slotChars[slot] = character;
                _slotTicks[slot] = ++_accessCounter;
                if (TerminalTextWidth.IsWide(character) && slot + 1 < Capacity)
                {
                    _slotPairTail[slot + 1] = true;
                    _slotTicks[slot + 1] = _slotTicks[slot];
                }
                _map[character] = slot;
                _bakedBitmap[character >> 3] |= (byte)(1 << (character & 7));
                _mapUpload[character] = (slot + 1) | (TerminalTextWidth.IsWide(character) ? int.MinValue : 0);
                MapVersion++;
                _nextSlot = Math.Max(_nextSlot, slot + 1);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool ShouldFallback(char character)
        => character < ' ' || character is >= '\u007F' and <= '\u009F' or >= '\uE000' and <= '\uF8FF';

    private static int[] CreateEmptyMap()
    {
        var map = new int[char.MaxValue + 1];
        Array.Fill(map, -1);
        return map;
    }

    private static GlyphUv[] BuildUvs()
    {
        var uvs = new GlyphUv[Capacity];
        for (var slot = 0; slot < Capacity; slot++)
        {
            var column = slot % Columns;
            var row = slot / Columns;
            uvs[slot] = new GlyphUv(
                column / (float)Columns,
                row / (float)Rows,
                (column + 1) / (float)Columns,
                (row + 1) / (float)Rows);
        }
        return uvs;
    }

    private void BakeGlyphs(DrawingCanvas canvas, char character, TextOptions options)
    {
        var text = character.ToString();
        if (TryBakeGlyphs(canvas, text, options))
            return;
        var retry = CreateOptions(text);
        retry.Origin = options.Origin;
        TryBakeGlyphs(canvas, text, retry);
    }

    private bool TryBakeGlyphs(DrawingCanvas canvas, string text, TextOptions options)
    {
        try
        {
            foreach (var glyph in TextBuilder.GenerateGlyphs(text, options))
                canvas.Fill(Brushes.Solid(Color.White), glyph.Paths);
            return true;
        }
        catch (Exception) when (RepairFallbacks(text[0]))
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool RepairFallbacks(char character)
    {
        lock (FallbackGate)
        {
            var current = FallbackFamilies;
            var usable = current.Where(family => CanRenderWith(family, character)).ToList();
            if (usable.Count == current.Count)
                return false;
            Volatile.Write(ref _fallbackOverride, usable);
            return true;
        }
    }

    private bool CanRenderWith(FontFamily family, char character)
    {
        try
        {
            return TextBuilder.GenerateGlyphs(character.ToString(), new TextOptions(family.CreateFont(_fontSize))).Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<FontFamily> FallbackFamilies
        => Volatile.Read(ref _fallbackOverride) ?? FallbackFamiliesLazy.Value;

    /** 文本里含主字体没有的字形时才挂回退链: 纯拉丁/框线文本不解析、不加载回退族。 */
    private TextOptions CreateOptions(string text)
        => new(_font)
        {
            FallbackFontFamilies = NeedsFallback(text) ? FallbackFamilies : [],
        };

    private bool NeedsFallback(string text)
    {
        try
        {
            foreach (var character in text)
            {
                if (character <= '\u007F' || _font.TryGetGlyphs(new CodePoint(character), out _))
                    continue;
                return true;
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static string DefaultCachePath(double fontSizePt)
    {
        var name = Math.Abs(fontSizePt - DefaultFontSizePt) < 1e-9
            ? "glyph-atlas.bin"
            : $"glyph-atlas-{fontSizePt}pt.bin";
        return Path.Combine(HarnessStorage.ResolveDefaultHome().CachePath, name);
    }

    /** 布局基线位置: 以 'H' 大写字母脚底(即基线)实测, 避免假设 SixLabors 的行盒口径。 */
    private float MeasureLayoutBaseline()
    {
        var glyphs = TextBuilder.GenerateGlyphs("H", new TextOptions(_font));
        return glyphs.Count > 0 ? glyphs[0].Bounds.Y + glyphs[0].Bounds.Height : GlyphHeight * 0.8f;
    }

    private static IReadOnlyList<FontFamily> ResolveFallbackFamilies()
    {
        // JetBrains 宿主进程(例如 Rider 启动的终端、测试、调试子进程)会劫持字体解析环境(Linux: FONTCONFIG_PATH 指向 JBR 内置 fontconfig;Windows 同样验证过),
        // 导致此处只能看到 JBR 自带字体,CJK 回退全部失效、宽字渲染为 tofu;真实终端会话不受影响,在该宿主内跑测试需显式恢复(如 FONTCONFIG_PATH=/etc/fonts)。
        var cachePath = GlyphFallbackCache.DefaultPath;
        var cacheKey = GlyphFallbackCache.Key(
            DefaultFontSize,
            [.. SystemFonts.Families.Select(family => family.Name).OrderBy(name => name, StringComparer.Ordinal)]);
        if (GlyphFallbackCache.TryLoad(cachePath, cacheKey) is { Count: > 0 } cachedNames)
        {
            var cachedFamilies = new List<FontFamily>(cachedNames.Count);
            foreach (var cachedName in cachedNames)
            {
                if (SystemFonts.TryGet(cachedName, out var cachedFamily))
                    cachedFamilies.Add(cachedFamily);
            }

            if (cachedFamilies.Count > 0)
                return cachedFamilies;
        }

        var families = new List<FontFamily>();
        foreach (var name in PlatformCandidateNames())
        {
            if (SystemFonts.TryGet(name, out var family) && IsUsableFamily(family))
                families.Add(family);
        }

        // 名字解析在部分环境(宿主机字体劫持/字体名与文件族名不一致)会漏掉真正的简中字体, 也可能让日文字体排在前面;
        // 以 CJK 区抽样覆盖数降序稳定排序, 让覆盖面最广的族优先(名称顺序仅作同分时的稳定次序)。
        // 评分要逐族加载字体(实测本机 30 个候选中每个 ~0.27s, 全评约 8s), 因此一旦出现满覆盖族就停止评分:
        // 满覆盖已是上限, 其后的族按名称优先级直接排在后面兜 emoji/符号(它们对 CJK 覆盖为 0, 评分也不会前移)。
        var ranked = new List<FontFamily>();
        var tail = new List<FontFamily>();
        var fullCoverage = false;
        foreach (var family in families)
        {
            if (fullCoverage)
            {
                tail.Add(family);
                continue;
            }

            ranked.Add(family);
            fullCoverage = Coverage(family) >= CjkProbeSamples;
        }

        var resolved = ranked.OrderByDescending(Coverage).ToList();
        GlyphFallbackCache.Save(cachePath, cacheKey, [.. resolved.Select(family => family.Name), .. tail.Select(family => family.Name)]);
        return [.. resolved, .. tail];
    }

    /** 候选字体族(顺序即优先级): 简体优先, 其次繁体/日文, 最后符号与 emoji。按平台只列本平台可能存在的族, 少做无谓的加载与评分。 */
    private static string[] PlatformCandidateNames() => OperatingSystem.IsWindows()
        ?
        [
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "DengXian",
            "DengXian Light",
            "SimSun",
            "SimHei",
            "KaiTi",
            "FangSong",
            "Microsoft JhengHei UI",
            "Microsoft JhengHei",
            "MS Gothic",
            "Noto Sans SC",
            "Segoe UI Symbol",
            "Segoe UI Emoji",
        ]
        : OperatingSystem.IsMacOS()
            ?
            [
                "PingFang SC",
                "Hiragino Sans GB",
                "Source Han Sans SC",
                "思源黑体",
                "Noto Sans SC",
                "Apple Color Emoji",
                "Noto Color Emoji",
            ]
            :
            [
                "Noto Sans SC",
                "Noto Serif SC",
                "Noto Sans CJK SC",
                "Noto Sans CJK TC",
                "Noto Sans CJK JP",
                "Noto Sans Mono CJK SC",
                "Noto Sans Mono CJK TC",
                "Source Han Sans SC",
                "思源黑体",
                "WenQuanYi Micro Hei",
                "WenQuanYi Zen Hei",
                "Droid Sans Fallback",
                "Noto Color Emoji",
            ];

    private const int CjkProbeStart = 0x4E00;
    private const int CjkProbeEnd = 0x9FFF;
    /** 抽样步长取与区块长度互质的质数, 保证样本均匀铺满整个 CJK 区。*/
    private const int CjkProbeStride = 37;

    /** CJK 抽样探针的样本数: 满覆盖(全部样本都命中)即评分的上限。 */
    private const int CjkProbeSamples = ((CjkProbeEnd - CjkProbeStart) / CjkProbeStride) + 1;

    private static int Coverage(FontFamily family)
    {
        try
        {
            var font = family.CreateFont(DefaultFontSize);
            var covered = 0;
            for (var codePoint = CjkProbeStart; codePoint <= CjkProbeEnd; codePoint += CjkProbeStride)
            {
                if (font.TryGetGlyphs(new CodePoint(codePoint), out _))
                    covered++;
            }
            return covered;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /** 渲染探针判定可用性: 只校验可加载不够——某些族(TryGetMetrics 成功)进 FallbackFontFamilies 后会让整条回退链产出空字形。 */
    private static bool IsUsableFamily(FontFamily family)
    {
        try
        {
            _ = TextBuilder.GenerateGlyphs("a", new TextOptions(family.CreateFont(DefaultFontSize)));
            _ = TextBuilder.GenerateGlyphs("😀", new TextOptions(family.CreateFont(DefaultFontSize)));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static FontFamily ResolveFontFamily()
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
                return family;
        }

        foreach (var family in SystemFonts.Families)
        {
            if (family.Name.Contains("Mono", StringComparison.OrdinalIgnoreCase) ||
                family.Name.Contains("Console", StringComparison.OrdinalIgnoreCase) ||
                family.Name.Contains("CJK", StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return SystemFonts.Get(SystemFonts.GetDefaultFamilyName());
    }
}
