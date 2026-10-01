using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Tui;

/**
 * 把窗口按键编码成 xterm 风格 VT 输入字节, 供 shell 窗格写进 PTY。
 * 只做编码: 控制键、方向键/编辑键/F 键、可打印字符(含 CJK 的 UTF-8)。
 */
internal static class TerminalKeyEncoder
{
    private const int BufferSize = 16;

    /** 编码到 destination, 返回写入长度; 没有对应字节时返回 0。 */
    public static int Encode(ConsoleKeyInfo key, Span<byte> destination, bool applicationCursorKeys = false)
    {
        if (destination.Length < BufferSize)
            throw new ArgumentException($"destination must be at least {BufferSize} bytes", nameof(destination));

        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        if (control && key.Key is >= ConsoleKey.A and <= ConsoleKey.Z)
        {
            destination[0] = (byte)(key.Key - ConsoleKey.A + 1);
            return 1;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter:
                destination[0] = (byte)'\r';
                return 1;
            case ConsoleKey.Tab when shift:
                return Write("\u001b[Z", destination);
            case ConsoleKey.Tab:
                destination[0] = (byte)'\t';
                return 1;
            case ConsoleKey.Backspace:
                destination[0] = 0x7f;
                return 1;
            case ConsoleKey.Escape:
                destination[0] = 0x1b;
                return 1;
            case ConsoleKey.Spacebar:
                destination[0] = (byte)' ';
                return 1;
            case ConsoleKey.UpArrow:
                return Arrow('A', destination, applicationCursorKeys);
            case ConsoleKey.DownArrow:
                return Arrow('B', destination, applicationCursorKeys);
            case ConsoleKey.RightArrow:
                return Arrow('C', destination, applicationCursorKeys);
            case ConsoleKey.LeftArrow:
                return Arrow('D', destination, applicationCursorKeys);
            case ConsoleKey.Home:
                return Arrow('H', destination, applicationCursorKeys);
            case ConsoleKey.End:
                return Arrow('F', destination, applicationCursorKeys);
            case ConsoleKey.Insert:
                return Write("\u001b[2~", destination);
            case ConsoleKey.Delete:
                return Write("\u001b[3~", destination);
            case ConsoleKey.PageUp:
                return Write("\u001b[5~", destination);
            case ConsoleKey.PageDown:
                return Write("\u001b[6~", destination);
            case >= ConsoleKey.F1 and <= ConsoleKey.F4:
                destination[0] = 0x1b;
                destination[1] = (byte)'O';
                destination[2] = (byte)('P' + (key.Key - ConsoleKey.F1));
                return 3;
            case >= ConsoleKey.F5 and <= ConsoleKey.F12:
                return Write($"\u001b[{15 + (int)key.Key - (int)ConsoleKey.F5}~", destination);
            default:
                break;
        }

        if (key.KeyChar == '\0')
            return 0;
        var character = key.KeyChar;
        return Encoding.UTF8.GetBytes(MemoryMarshal.CreateSpan(ref character, 1), destination);
    }

    private static int Arrow(char final, Span<byte> destination, bool applicationCursorKeys)
    {
        destination[0] = 0x1b;
        destination[1] = applicationCursorKeys ? (byte)'O' : (byte)'[';
        destination[2] = (byte)final;
        return 3;
    }

    private static int Write(string text, Span<byte> destination)
        => Encoding.ASCII.GetBytes(text, destination);
}
