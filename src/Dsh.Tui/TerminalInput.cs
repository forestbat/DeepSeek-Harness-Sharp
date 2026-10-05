using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Tui;

/** 鼠标/指针事件: Pressed 表示有键按下(拖动中的移动也为真); IsMove 区分移动与按下/释放。 */
public readonly record struct TerminalMouseEvent(int X, int Y, int Button, bool Pressed, bool IsMove = false)
{
    public bool IsWheel => Button is 64 or 65;

    public bool IsDrag => IsMove && Button != 3;

    public int WheelDelta => Button switch
    {
        64 => 1,
        65 => -1,
        _ => 0,
    };
}

public readonly record struct TerminalInputEvent(ConsoleKeyInfo Key, TerminalMouseEvent? Mouse, string? Paste = null)
{
    public bool IsMouse => Mouse is not null;

    public bool IsPaste => Paste is not null;

    public static TerminalInputEvent FromKey(ConsoleKeyInfo key) => new(key, null);

    public static TerminalInputEvent FromMouse(TerminalMouseEvent mouse) => new(default, mouse);

    public static TerminalInputEvent FromPaste(string text) => new(default, null, text);
}

/** 平台无关的输入源: Unix 走原始字节 + SGR, Windows 走控制台输入记录; 两者产出同一事件类型。 */
internal interface ITerminalInputSource : IDisposable
{
    /**
     * 阻塞直到一个键或鼠标事件; timeoutMs 超时或被 Wake 唤醒返回 null。
     * 返回 null 后用 EndOfStream 区分流结束(结束才退出主循环)。
     */
    TerminalInputEvent? Read(int timeoutMs);

    bool EndOfStream { get; }

    /** 从任意线程唤醒阻塞中的 Read, 用于 UI 事件驱动的重绘。 */
    void Wake();
}

/**
 * 原始字节增量解码: 键盘转义序列与鼠标报文, 跨读取块保持状态。
 * 鼠标报文由 MouseReport 整条识别并丢弃/还原 —— 绝不允许报文里的字节落成"键入文本"。
 */
public sealed class TerminalInputParser
{
    private const byte Escape = 0x1b;
    private static readonly byte[] PasteStart = "\u001b[200~"u8.ToArray();
    private static readonly byte[] PasteEnd = "\u001b[201~"u8.ToArray();
    private const int MaxPasteBytes = 8 * 1024 * 1024;
    private readonly List<byte> _pending = [];
    private readonly Queue<TerminalInputEvent> _ready = new();

    /** 只挂着一个孤立 ESC: 调用方可以用更短的超时把它当真实按键冲刷出来。 */
    public bool HasPendingEscape => _pending.Count == 1 && _pending[0] == Escape;

    public void Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
            _pending.Add(value);
    }

    /** 静默期到达: 挂起的孤立 ESC 是用户真的按了 Esc(不是被切开的报文报头), 当按键交出。 */
    public bool TryFlushPendingEscape(out TerminalInputEvent input)
    {
        input = default;
        if (!HasPendingEscape)
            return false;
        _pending.Clear();
        input = TerminalInputEvent.FromKey(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false));
        return true;
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
            if (StartsWithPasteStart())
            {
                var end = IndexOf(PasteEnd, PasteStart.Length);
                if (end >= 0)
                {
                    var text = Encoding.UTF8.GetString(
                        CollectionsMarshal.AsSpan(_pending).Slice(PasteStart.Length, end - PasteStart.Length));
                    _pending.RemoveRange(0, end + PasteEnd.Length);
                    input = TerminalInputEvent.FromPaste(text);
                    return true;
                }
                if (_pending.Count <= MaxPasteBytes)
                    return false;   // 等 201~ 结束标记
                // 超长且无结束标记: 落到普通解析(丢标记, 其余当文本), 不永久挂起
            }

            var first = _pending[0];
            if (first == Escape || first == (byte)'[')
            {
                var match = MouseReport.Match(CollectionsMarshal.AsSpan(_pending));
                if (match.Status == MouseReportStatus.Matched)
                {
                    _pending.RemoveRange(0, match.Consumed);
                    input = TerminalInputEvent.FromMouse(match.Mouse);
                    return true;
                }

                if (match.Status == MouseReportStatus.NeedMore && _pending.Count <= MouseReport.MaxLength)
                    return false;   // 等报文剩余字节; 超长会落到下面的普通解析, 不吞用户输入
            }

            if (first == Escape)
            {
                if (_pending.Count < 2)
                    return false;   // 只有 ESC: 挂起, 等下一个字节或静默期冲刷
                if (!TryParseEscape(out input, out var consumed))
                    return false;
                _pending.RemoveRange(0, consumed);
                if (input.IsMouse || input.Key.KeyChar != '\0' || input.Key.Key != ConsoleKey.NoName)
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

    private bool StartsWithPasteStart()
        => _pending.Count >= PasteStart.Length
            && CollectionsMarshal.AsSpan(_pending)[..PasteStart.Length].SequenceEqual(PasteStart);

    private int IndexOf(byte[] pattern, int from)
    {
        var span = CollectionsMarshal.AsSpan(_pending);
        for (var index = from; index + pattern.Length <= span.Length; index++)
        {
            if (span.Slice(index, pattern.Length).SequenceEqual(pattern))
                return index;
        }
        return -1;
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
        input = TerminalInputEvent.FromKey(CsiKey((char)_pending[end], parameters));
        consumed = end + 1;
        return true;
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

/** 阻塞读取原始字节并逐个解码为键或鼠标事件; 仅在 Unix 终端后端使用。自管道承载 UI 重绘的跨线程唤醒。 */
internal sealed class TerminalInputReader : ITerminalInputSource
{
    private const short PollIn = 0x0001;
    private const int FSetFl = 4;
    private const int ONonBlock = 0x800;
    private const int StdinFd = 0;

    /** 挂起孤立 ESC 时的等待上限: 超过它还没等到后续字节, 就认为用户真的按了 Esc。 */
    private const int EscapeFlushMilliseconds = 30;

    private static readonly byte[] WakeByte = [0];

    private readonly TerminalInputParser _parser = new();
    private readonly byte[] _buffer = new byte[1024];
    private readonly byte[] _wakeDrain = new byte[64];
    private readonly int _wakeRead;
    private readonly int _wakeWrite;
    private bool _disposed;

    public TerminalInputReader()
    {
        var pipeFds = new int[2];
        if (pipe(pipeFds) != 0)
            throw new InvalidOperationException($"pipe() failed, errno={Marshal.GetLastWin32Error()}");
        (_wakeRead, _wakeWrite) = (pipeFds[0], pipeFds[1]);
        fcntl(_wakeRead, FSetFl, ONonBlock);
        fcntl(_wakeWrite, FSetFl, ONonBlock);
    }

    public bool EndOfStream { get; private set; }

    public TerminalInputEvent? Read(int timeoutMs)
    {
        while (true)
        {
            if (_parser.TryParse(out var inputEvent))
                return inputEvent;
            if (EndOfStream)
                return null;
            var fds = new[]
            {
                new PollFd { Fd = 0, Events = PollIn },
                new PollFd { Fd = _wakeRead, Events = PollIn },
            };
            var wait = _parser.HasPendingEscape ? Math.Min(timeoutMs, EscapeFlushMilliseconds) : timeoutMs;
            var ready = poll(fds, fds.Length, wait);
            if (ready == 0)
            {
                if (_parser.TryFlushPendingEscape(out var escape))
                    return escape;
                return null;
            }

            if (ready < 0)
            {
                EndOfStream = true;
                return null;
            }

            if (fds[1].Revents != 0)
            {
                DrainWake();
                return null;
            }

            if (fds[0].Revents == 0)
                continue;
            // 直接 read(2) 读 fd 0: 经 .NET 的 Console 流读 pty 在 Linux 上会阻塞不返回(实测 poll 报可读后流读仍挂住)
            var read = readBytes(StdinFd, _buffer, _buffer.Length);
            if (read <= 0)
            {
                EndOfStream = true;
                return null;
            }

            _parser.Append(_buffer.AsSpan(0, read));
        }
    }

    public void Wake()
    {
        if (!_disposed)
            write(_wakeWrite, WakeByte, 1);
    }

    private void DrainWake()
    {
        while (readBytes(_wakeRead, _wakeDrain, _wakeDrain.Length) > 0)
        {
        }
    }

    public void Dispose()
    {
        _disposed = true;
        close(_wakeRead);
        close(_wakeWrite);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern int readBytes(int fd, [Out] byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, int nfds, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int pipe([Out] int[] pipefd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
