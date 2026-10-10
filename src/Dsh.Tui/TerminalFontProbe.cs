using System.Diagnostics;
using System.Text;

namespace Dsh.Tui;

/**
 * 启动时向终端查询字符格子的像素尺寸(CSI 16 t, 应答 ESC [ 6 ; 高 ; 宽 t), 供 GPU 渲染路线对齐终端字号。
 * 终端不支持/超时/重定向时返回 null, 调用方落默认值。GPU 窗口想换字号 = 用户去调终端字号, 重启 dsharp。
 */
public static class TerminalFontProbe
{
    private const int QueryTimeoutMilliseconds = 300;

    public static (int Width, int Height)? QueryCellPixelSize()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return null;
        try
        {
            using var raw = TerminalRawMode.TryEnable();
            Console.Out.Write("\x1b[16t");
            Console.Out.Flush();
            return Parse(ReadReply());
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string ReadReply()
    {
        var builder = new StringBuilder();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < QueryTimeoutMilliseconds)
        {
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(5);
                continue;
            }
            var key = Console.ReadKey(intercept: true);
            builder.Append(key.KeyChar);
            if (key.KeyChar == 't')
                break;
        }
        return builder.ToString();
    }

    /** 应答格式 ESC [ 6 ; 高 ; 宽 t; 解析失败返回 null。 */
    internal static (int Width, int Height)? Parse(string reply)
    {
        const string prefix = "\x1b[6;";
        var start = reply.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;
        var cursor = start + prefix.Length;
        var height = ReadNumber(reply, ref cursor);
        if (height is null || cursor >= reply.Length || reply[cursor] != ';')
            return null;
        cursor++;
        var width = ReadNumber(reply, ref cursor);
        if (width is null || cursor >= reply.Length || reply[cursor] != 't')
            return null;
        return width > 0 && height > 0 ? (width.Value, height.Value) : null;
    }

    private static int? ReadNumber(string text, ref int cursor)
    {
        var start = cursor;
        while (cursor < text.Length && char.IsAsciiDigit(text[cursor]))
            cursor++;
        return cursor > start && int.TryParse(text.AsSpan(start, cursor - start), out var value) ? value : null;
    }
}
