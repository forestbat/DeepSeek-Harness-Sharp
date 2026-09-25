using System.Text;

namespace Dsh.Tui;

public readonly record struct TerminalMouseEvent(int X, int Y, int Button, bool Pressed)
{
    public bool IsWheel => Button is 64 or 65;

    public int WheelDelta => Button switch
    {
        64 => 1,
        65 => -1,
        _ => 0,
    };
}

public readonly record struct TerminalInputEvent(ConsoleKeyInfo Key, TerminalMouseEvent? Mouse)
{
    public bool IsMouse => Mouse is not null;

    public static TerminalInputEvent FromKey(ConsoleKeyInfo key) => new(key, null);

    public static TerminalInputEvent FromMouse(TerminalMouseEvent mouse) => new(default, mouse);
}

/** 原始字节增量解码: 键盘转义序列与 SGR 鼠标事件, 跨读取块保持状态。 */
public sealed class TerminalInputParser
{
    private const byte Escape = 0x1b;
    private readonly List<byte> _pending = [];
    private readonly Queue<TerminalInputEvent> _ready = new();

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            _pending.Add(value);
    }

    public bool TryParse(out TerminalInputEvent input)
    {
        while (true)
        {
            if (_ready.Count > 0)
            {
                input = _ready.Dequeue();
                return true;
            }

            input = default;
            if (_pending.Count == 0)
                return false;
            var first = _pending[0];
            if (first == Escape)
            {
                if (!TryParseEscape(out input, out var consumed))
                    return false;
                _pending.RemoveRange(0, consumed);
                if (input.Mouse is not null || input.Key.KeyChar != '\0' || input.Key.Key != ConsoleKey.NoName)
                    return true;
                continue;
            }

            if (first < 0x20 || first == 0x7f)
            {
                _pending.RemoveAt(0);
                input = TerminalInputEvent.FromKey(ControlKey(first));
                return true;
            }

            if (!TryTakeUtf8(out var chars))
                return false;
            if (chars.Length == 0)
            {
                _pending.RemoveAt(0);
                continue;
            }

            _pending.RemoveRange(0, Encoding.UTF8.GetByteCount(chars));
            for (var index = 1; index < chars.Length; index++)
                _ready.Enqueue(TerminalInputEvent.FromKey(PrintableKey(chars[index])));
            input = TerminalInputEvent.FromKey(PrintableKey(chars[0]));
            return true;
        }
    }

    private bool TryTakeUtf8(out string chars)
    {
        chars = "";
        var first = _pending[0];
        var length = first switch
        {
            < 0x80 => 1,
            >= 0xC2 and < 0xE0 => 2,
            >= 0xE0 and < 0xF0 => 3,
            >= 0xF0 and < 0xF5 => 4,
            _ => 1,
        };
        if (_pending.Count < length)
            return false;
        chars = Encoding.UTF8.GetString(_pending.ToArray(), 0, length);
        if (chars.Length == 1 && chars[0] == '\uFFFD')
            chars = "";
        return true;
    }

    private bool TryParseEscape(out TerminalInputEvent input, out int consumed)
    {
        input = default;
        consumed = 0;
        if (_pending.Count < 2)
        {
            consumed = 1;
            input = TerminalInputEvent.FromKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
            return true;
        }

        var second = _pending[1];
        if (second == (byte)'[')
            return TryParseCsi(out input, out consumed);
        if (second == (byte)'O')
        {
            if (_pending.Count < 3)
                return false;
            consumed = 3;
            input = TerminalInputEvent.FromKey(Ss3Key((char)_pending[2]));
            return true;
        }

        consumed = 2;
        var character = (char)second;
        input = TerminalInputEvent.FromKey(new ConsoleKeyInfo(character, ConsoleKey.NoName, false, true, false));
        return true;
    }

    private bool TryParseCsi(out TerminalInputEvent input, out int consumed)
    {
        input = default;
        consumed = 0;
        var end = -1;
        for (var index = 2; index < _pending.Count; index++)
        {
            var value = _pending[index];
            if (value >= 0x40 && value <= 0x7e)
            {
                end = index;
                break;
            }
        }
        if (end < 0)
            return false;

        var parameters = Encoding.ASCII.GetString(_pending.ToArray(), 2, end - 2);
        var final = (char)_pending[end];
        consumed = end + 1;
        if ((final == 'M' || final == 'm') && parameters.StartsWith('<'))
        {
            input = TerminalInputEvent.FromMouse(ParseSgrMouse(parameters, final == 'M'));
            return true;
        }

        input = TerminalInputEvent.FromKey(CsiKey(final, parameters));
        return true;
    }

    private static TerminalMouseEvent ParseSgrMouse(string parameters, bool pressed)
    {
        var parts = parameters[1..].Split(';');
        var button = parts.Length > 0 && int.TryParse(parts[0], out var value) ? value : 0;
        var x = parts.Length > 1 && int.TryParse(parts[1], out var px) ? px - 1 : 0;
        var y = parts.Length > 2 && int.TryParse(parts[2], out var py) ? py - 1 : 0;
        return new TerminalMouseEvent(x, y, button, pressed);
    }

    private static ConsoleKeyInfo ControlKey(byte value)
    {
        switch (value)
        {
            case 0x0d or 0x0a:
                return new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false);
            case 0x09:
                return new ConsoleKeyInfo('\t', ConsoleKey.Tab, false, false, false);
            case 0x08 or 0x7f:
                return new ConsoleKeyInfo('\0', ConsoleKey.Backspace, false, false, false);
            default:
                return value is >= 1 and <= 26
                    ? new ConsoleKeyInfo('\0', ConsoleKey.A + (value - 1), false, false, true)
                    : new ConsoleKeyInfo((char)value, ConsoleKey.NoName, false, false, true);
        }
    }

    private static ConsoleKeyInfo PrintableKey(char character)
        => character == ' '
            ? new ConsoleKeyInfo(' ', ConsoleKey.Spacebar, false, false, false)
            : new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false);

    private static ConsoleKeyInfo Ss3Key(char final) => final switch
    {
        'A' => new ConsoleKeyInfo('\0', ConsoleKey.UpArrow, false, false, false),
        'B' => new ConsoleKeyInfo('\0', ConsoleKey.DownArrow, false, false, false),
        'C' => new ConsoleKeyInfo('\0', ConsoleKey.RightArrow, false, false, false),
        'D' => new ConsoleKeyInfo('\0', ConsoleKey.LeftArrow, false, false, false),
        'H' => new ConsoleKeyInfo('\0', ConsoleKey.Home, false, false, false),
        'F' => new ConsoleKeyInfo('\0', ConsoleKey.End, false, false, false),
        _ => default,
    };

    private static ConsoleKeyInfo CsiKey(char final, string parameters)
    {
        var key = final switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            '~' => parameters switch
            {
                "1" or "7" => ConsoleKey.Home,
                "2" => ConsoleKey.Insert,
                "3" => ConsoleKey.Delete,
                "4" or "8" => ConsoleKey.End,
                "5" => ConsoleKey.PageUp,
                "6" => ConsoleKey.PageDown,
                _ => ConsoleKey.NoName,
            },
            _ => ConsoleKey.NoName,
        };
        return new ConsoleKeyInfo('\0', key, false, false, false);
    }
}

/** 阻塞读取原始字节并逐个解码为键或鼠标事件; 仅在 Unix 终端后端使用。 */
internal sealed class TerminalInputReader(Stream input) : IDisposable
{
    private readonly TerminalInputParser _parser = new();
    private readonly byte[] _buffer = new byte[1024];

    public TerminalInputEvent? Read()
    {
        while (true)
        {
            if (_parser.TryParse(out var inputEvent))
                return inputEvent;
            var read = input.Read(_buffer, 0, _buffer.Length);
            if (read <= 0)
                return null;
            _parser.Append(_buffer.AsSpan(0, read));
        }
    }

    public void Dispose() => input.Dispose();
}
