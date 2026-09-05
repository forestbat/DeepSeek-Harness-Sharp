using System.Text;

namespace Dsh.Jobs;

internal static class Utf8Retention
{
    private static readonly UTF8Encoding LenientUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    public static int ByteCount(string text) => LenientUtf8.GetByteCount(text);

    public static string RetainTail(string text, int maxBytes)
    {
        if (ByteCount(text) <= maxBytes) return text;
        var bytes = LenientUtf8.GetBytes(text);
        var tail = bytes.AsSpan(bytes.Length - maxBytes).ToArray();
        return LenientUtf8.GetString(TrimLeadingContinuation(tail));
    }

    public static string RetainHead(string text, int maxBytes)
    {
        if (ByteCount(text) <= maxBytes) return text;
        var bytes = LenientUtf8.GetBytes(text);
        var head = bytes.AsSpan(0, maxBytes).ToArray();
        return LenientUtf8.GetString(TrimTrailingPartial(head));
    }

    private static byte[] TrimTrailingPartial(byte[] bytes)
    {
        var i = bytes.Length - 1;
        while (i >= 0 && (bytes[i] & 0xc0) == 0x80 && bytes.Length - i <= 3) i--;
        if (i < 0) return bytes;
        var lead = bytes[i];
        var expected = lead < 0x80 ? 1 : lead < 0xe0 ? 2 : lead < 0xf0 ? 3 : lead < 0xf8 ? 4 : 0;
        if (expected == 0) return bytes;
        return bytes.Length - i < expected ? bytes[..i] : bytes;
    }

    private static byte[] TrimLeadingContinuation(byte[] bytes)
    {
        var i = 0;
        while (i < bytes.Length && (bytes[i] & 0xc0) == 0x80) i++;
        return bytes[i..];
    }
}
