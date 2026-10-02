using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace Dsh.Tui;

/** 终端图像协议: kitty graphics 优先, sixel 兜底; 都不支持时调用方回退 CPU(ANSI)渲染。 */
internal enum TerminalGraphicsProtocol
{
    None,
    Kitty,
    Sixel,
}

/**
 * 终端图像协议的编码与能力探测: 把 GL 读回的 RGBA 帧贴到终端。
 * kitty 按终端行分块(只重传变化的行, 高帧率); sixel 整帧重发(无局部更新语义)。
 */
internal static class TerminalGraphics
{
    private const int ChunkSize = 4096;
    private const int QueryId = 0x6473683F;
    private const int KittyTimeoutMilliseconds = 300;

    /** 环境变量能直接判定的 kitty 支持情况(免查询): kitty / wezterm / ghostty。 */
    public static bool EnvSupportsKitty()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KITTY_WINDOW_ID")))
            return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WEZTERM_PANE")))
            return true;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GHOSTTY_RESOURCES_DIR")))
            return true;
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        return term.Contains("kitty", StringComparison.OrdinalIgnoreCase)
            || term.Contains("wezterm", StringComparison.OrdinalIgnoreCase);
    }

    /**
     * 探测协议: 环境命中直接判 kitty; 否则同时发 kitty 查询与 DA1(设备属性)查询并等应答。
     */
    public static TerminalGraphicsProtocol Detect(out string reason)
    {
        if (EnvSupportsKitty())
        {
            reason = "";
            return TerminalGraphicsProtocol.Kitty;
        }
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            reason = "终端输入/输出被重定向, 无法探测图像协议";
            return TerminalGraphicsProtocol.None;
        }
        try
        {
            using var raw = TerminalRawMode.TryEnable();
            Console.Out.Write($"\x1b_Gi={QueryId},s=1,v=1,a=q,t=d,f=24;AAAA\x1b\\\x1b[c");
            Console.Out.Flush();
            var reply = ReadReply();
            if (ParseKittyReply(reply, QueryId))
            {
                reason = "";
                return TerminalGraphicsProtocol.Kitty;
            }
            if (ParseSixelSupport(reply))
            {
                reason = "";
                return TerminalGraphicsProtocol.Sixel;
            }
            reason = $"终端不支持 kitty graphics / sixel(终端应答: {Escape(reply)})";
            return TerminalGraphicsProtocol.None;
        }
        catch (IOException error)
        {
            reason = $"图像协议探测失败: {error.Message}";
            return TerminalGraphicsProtocol.None;
        }
    }

    /** kitty 应答形如 ESC _ G i=<id> ; OK ESC \; 只认自身 id。 */
    internal static bool ParseKittyReply(string reply, int id)
    {
        var start = reply.IndexOf("\x1b_G", StringComparison.Ordinal);
        if (start < 0)
            return false;
        var end = reply.IndexOf("\x1b\\", start, StringComparison.Ordinal);
        if (end < 0)
            return false;
        var body = reply[(start + 3)..end];
        return body.Contains($"i={id}", StringComparison.Ordinal)
            && body.Contains(";OK", StringComparison.Ordinal);
    }

    /** DA1 应答形如 ESC [ ? 1 ; 2 ; 4 c; 参数含 4 表示 sixel。 */
    internal static bool ParseSixelSupport(string reply)
    {
        var start = reply.IndexOf("\x1b[?", StringComparison.Ordinal);
        if (start < 0)
            return false;
        var end = reply.IndexOf('c', start);
        if (end < 0)
            return false;
        var parameters = reply[(start + 3)..end].Split(';');
        return parameters.Contains("4", StringComparer.Ordinal);
    }

    /**
     * kitty 一行图像(按终端行分块, 只重传变化的行): 压缩该行带的 RGBA, 传输并放置到指定终端行。
     * 每行一个固定 imageId = Base + row, 复放置不叠加; 光标先移到该行首格。
     */
    public static string EncodeKittyRow(byte[] rgba, int width, int height, int row, int rowHeight, int columns)
    {
        var builder = new StringBuilder();
        builder.Append($"\x1b[{row + 1};1H");
        var imageId = KittyRowImageBase + row;
        var payload = Convert.ToBase64String(CompressBand(rgba, width, height, row * rowHeight, rowHeight));
        var control = $"a=t,i={imageId},f=32,o=z,s={width},t={rowHeight},q=2";
        var chunks = Math.Max(1, (payload.Length + ChunkSize - 1) / ChunkSize);
        for (var index = 0; index < chunks; index++)
        {
            var chunk = payload.Substring(index * ChunkSize, Math.Min(ChunkSize, payload.Length - index * ChunkSize));
            var more = index == chunks - 1 ? 0 : 1;
            builder.Append(index == 0
                ? $"\x1b_G{control},m={more};{chunk}\x1b\\"
                : $"\x1b_Gm={more};{chunk}\x1b\\");
        }
        builder.Append($"\x1b_Ga=p,i={imageId},p=1,c={columns},r=1,q=2,C=1\x1b\\");
        return builder.ToString();
    }

    public const int KittyRowImageBase = 0x64736800;

    /** 删除所有行图像(退出时清理)。 */
    public static string DeleteRows(int rowCount)
    {
        var builder = new StringBuilder();
        for (var row = 0; row < rowCount; row++)
            builder.Append($"\x1b_Ga=d,d=i,i={KittyRowImageBase + row},q=2\x1b\\");
        return builder.ToString();
    }

    /**
     * sixel 整帧编码: 精确调色板(≤255 色), 超限退化为 RGB332 量化; 6 行一带, 逐色 RLE。
     * 输出前把光标移到左上角, 图像按自身像素尺寸铺满(与终端格对齐)。
     */
    public static string EncodeSixel(byte[] rgba, int width, int height)
    {
        var (indices, palette) = Quantize(rgba, width, height);
        var bandCount = (height + 5) / 6;
        var bandColors = new List<int>[bandCount];
        for (var band = 0; band < bandCount; band++)
            bandColors[band] = [];
        var seen = new HashSet<int>[bandCount];
        for (var band = 0; band < bandCount; band++)
            seen[band] = [];
        for (var y = 0; y < height; y++)
        {
            var band = y / 6;
            for (var x = 0; x < width; x++)
            {
                var color = indices[(y * width) + x];
                if (seen[band].Add(color))
                    bandColors[band].Add(color);
            }
        }

        var builder = new StringBuilder();
        builder.Append("\x1b[H\x1bPq");
        builder.Append($"\"1;1;{width};{height}");
        foreach (var (color, index) in palette)
            builder.Append($"#{index};2;{(color >> 16) & 0xFF};{(color >> 8) & 0xFF};{color & 0xFF}");
        for (var band = 0; band < bandCount; band++)
        {
            for (var colorPosition = 0; colorPosition < bandColors[band].Count; colorPosition++)
            {
                if (colorPosition > 0)
                    builder.Append('$');
                var color = bandColors[band][colorPosition];
                builder.Append($"#{color}");
                AppendSixelRun(builder, indices, width, height, band, color);
            }
            if (band < bandCount - 1)
                builder.Append('-');
        }
        builder.Append("\x1b\\");
        return builder.ToString();
    }

    private static void AppendSixelRun(StringBuilder builder, byte[] indices, int width, int height, int band, int color)
    {
        var current = -1;
        var repeat = 0;
        for (var x = 0; x < width; x++)
        {
            var mask = 0;
            for (var bit = 0; bit < 6; bit++)
            {
                var y = (band * 6) + bit;
                if (y < height && indices[(y * width) + x] == color)
                    mask |= 1 << bit;
            }
            if (mask == current)
            {
                repeat++;
                continue;
            }
            if (current >= 0)
                AppendSixelValue(builder, current, repeat);
            current = mask;
            repeat = 1;
        }
        if (current >= 0)
            AppendSixelValue(builder, current, repeat);
    }

    private static void AppendSixelValue(StringBuilder builder, int mask, int repeat)
    {
        var character = (char)(63 + mask);
        if (repeat >= 4)
            builder.Append('!').Append(repeat).Append(character);
        else
            builder.Append(character, repeat);
    }

    /** 精确调色板优先; 超过 255 色退化为 RGB332 量化。返回每像素调色板索引与索引→RGB 表。 */
    private static (byte[] Indices, List<(int Color, int Index)> Palette) Quantize(byte[] rgba, int width, int height)
    {
        var indices = new byte[width * height];
        var exact = new Dictionary<int, int>();
        var ordered = new List<(int Color, int Index)>();
        var overflow = false;
        for (var i = 0; i < width * height; i++)
        {
            var offset = i * 4;
            var color = (rgba[offset] << 16) | (rgba[offset + 1] << 8) | rgba[offset + 2];
            if (!exact.TryGetValue(color, out var index))
            {
                if (exact.Count >= 255)
                {
                    overflow = true;
                    break;
                }
                index = exact.Count;
                exact[color] = index;
                ordered.Add((color, index));
            }
            indices[i] = (byte)index;
        }
        if (!overflow)
            return (indices, ordered);
        var quantized = new (int Color, int Index)[256];
        for (var i = 0; i < 256; i++)
        {
            var red = ((i >> 5) & 0x7) * 255 / 7;
            var green = ((i >> 2) & 0x7) * 255 / 7;
            var blue = (i & 0x3) * 255 / 3;
            quantized[i] = ((red << 16) | (green << 8) | blue, i);
        }
        for (var i = 0; i < width * height; i++)
        {
            var offset = i * 4;
            indices[i] = (byte)(((rgba[offset] >> 5) << 5) | ((rgba[offset + 1] >> 5) << 2) | (rgba[offset + 2] >> 6));
        }
        return (indices, [.. quantized]);
    }

    /** 把终端应答转成可读短串(控制字符转义, 截断), 便于诊断报告。 */
    private static string Escape(string text)
    {
        var builder = new StringBuilder();
        foreach (var character in text)
        {
            if (builder.Length >= 80)
            {
                builder.Append('…');
                break;
            }
            switch (character)
            {
                case '\x1b':
                    builder.Append("\\e");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(character))
                        builder.Append($"\\x{(int)character:X2}");
                    else
                        builder.Append(character);
                    break;
            }
        }
        return builder.Length == 0 ? "(空)" : builder.ToString();
    }

    /** 取自上而下的第 topY 行起、高 bandHeight 的像素带并压缩(GL 缓冲自下而上, 需换算并按上→下写入)。 */
    private static byte[] CompressBand(byte[] rgba, int width, int height, int topY, int bandHeight)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var top = height - 1 - topY;
            var bottom = Math.Max(0, height - topY - bandHeight);
            for (var y = top; y >= bottom; y--)
                zlib.Write(rgba, y * width * 4, width * 4);
        }
        return output.ToArray();
    }

    private static string ReadReply()
    {
        var builder = new StringBuilder();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < KittyTimeoutMilliseconds)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(5);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            builder.Append(key.KeyChar);
            var text = builder.ToString();
            if (text.Contains("\x1b\\", StringComparison.Ordinal) && text.Contains('c', StringComparison.Ordinal))
                break;
        }
        return builder.ToString();
    }
}
