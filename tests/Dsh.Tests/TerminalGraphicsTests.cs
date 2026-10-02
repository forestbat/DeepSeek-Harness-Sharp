using System.IO.Compression;
using System.Text;
using Dsh.Tui;
using Xunit;

namespace Dsh.Tests;

public class TerminalGraphicsTests
{
    [Fact]
    public void EncodeKittyRow_TransmitsAndPlaces()
    {
        var pixels = Pixels(2, 2);
        var text = TerminalGraphics.EncodeKittyRow(pixels, 2, 2, row: 0, rowHeight: 2, columns: 2);
        Assert.StartsWith("\x1b[1;1H", text, StringComparison.Ordinal);
        Assert.Contains("a=t", text, StringComparison.Ordinal);
        Assert.Contains("c=2", text, StringComparison.Ordinal);
        Assert.Contains("a=p", text, StringComparison.Ordinal);
        Assert.Equal(FlipRows(pixels, 2, 2), Decode(text));
    }

    [Fact]
    public void EncodeKittyRow_OnlyCoversItsOwnBand()
    {
        var pixels = Pixels(2, 4);
        var text = TerminalGraphics.EncodeKittyRow(pixels, 2, 4, row: 1, rowHeight: 2, columns: 2);
        Assert.StartsWith("\x1b[2;1H", text, StringComparison.Ordinal);
        Assert.Equal(Band(pixels, 2, 4, topY: 2, bandHeight: 2), Decode(text));
    }

    [Fact]
    public void DeleteRows_EmitsOneDeletePerRow()
    {
        var text = TerminalGraphics.DeleteRows(3);
        Assert.Equal(3, text.Split("a=d", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void ParseKittyReply_OnlyOwnOkReply()
    {
        Assert.True(TerminalGraphics.ParseKittyReply("\x1b_Gi=7;OK\x1b\\", 7));
        Assert.False(TerminalGraphics.ParseKittyReply("\x1b_Gi=8;OK\x1b\\", 7));
        Assert.False(TerminalGraphics.ParseKittyReply("noise", 7));
        Assert.False(TerminalGraphics.ParseKittyReply("\x1b_Gi=7;ENOENT\x1b\\", 7));
    }

    [Fact]
    public void ParseSixelSupport_RequiresAttribute4()
    {
        Assert.True(TerminalGraphics.ParseSixelSupport("\x1b[?1;2;4c"));
        Assert.False(TerminalGraphics.ParseSixelSupport("\x1b[?1;2c"));
        Assert.False(TerminalGraphics.ParseSixelSupport("noise"));
    }

    [Fact]
    public void EncodeSixel_HasHeaderPaletteAndTerminator()
    {
        var text = TerminalGraphics.EncodeSixel(Pixels(2, 2), 2, 2);
        Assert.Contains("\x1bPq", text, StringComparison.Ordinal);
        Assert.Contains("\"1;1;2;2", text, StringComparison.Ordinal);
        Assert.Contains("#0;2;", text, StringComparison.Ordinal);
        Assert.EndsWith("\x1b\\", text, StringComparison.Ordinal);
    }

    private static byte[] Pixels(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = ((y * width) + x) * 4;
                pixels[offset] = (byte)(y * 16 + x);
                pixels[offset + 1] = (byte)(0x40 + x);
                pixels[offset + 2] = (byte)(0x80 + y);
                pixels[offset + 3] = 0xFF;
            }
        }
        return pixels;
    }

    private static byte[] Band(byte[] pixels, int width, int height, int topY, int bandHeight)
    {
        var band = new byte[width * bandHeight * 4];
        for (var ty = 0; ty < bandHeight; ty++)
            Array.Copy(pixels, ((height - 1 - (topY + ty)) * width) * 4, band, ty * width * 4, width * 4);
        return band;
    }

    private static byte[] FlipRows(byte[] pixels, int width, int height)
    {
        var flipped = new byte[pixels.Length];
        for (var y = 0; y < height; y++)
            Array.Copy(pixels, y * width * 4, flipped, ((height - 1 - y) * width) * 4, width * 4);
        return flipped;
    }

    /** 取出所有带载荷的 ESC_G 转义, 拼接 base64 后 zlib 解压。 */
    private static byte[] Decode(string text)
    {
        var payload = new StringBuilder();
        var cursor = 0;
        while (true)
        {
            var start = text.IndexOf("\x1b_G", cursor, StringComparison.Ordinal);
            if (start < 0)
                break;
            var end = text.IndexOf("\x1b\\", start, StringComparison.Ordinal);
            Assert.True(end > start, "kitty 转义未闭合");
            var semicolon = text.IndexOf(';', start);
            if (semicolon > start && semicolon < end)
                payload.Append(text, semicolon + 1, end - semicolon - 1);
            cursor = end + 2;
        }
        using var input = new MemoryStream(Convert.FromBase64String(payload.ToString()));
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

}
