using System.Diagnostics;
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
 * 鼠标报文由 MouseReport 整条识别并丢弃/还原 -- 绝不允许报文里的字节落成"键入文本"。
 */
public sealed class TerminalInputParser
{
    private const byte Escape = 0x1b;
    private static readonly byte[] PasteStart = "\u001b[200~"u8.ToArray();
    private static readonly byte[] PasteEnd = "\u001b[201~"u8.ToArray();
    private const int MaxPasteBytes = 8 * 1024 * 1024;

    /** xterm 修饰键参数 = 1 + 位掩码: Shift=1、Alt=2、Ctrl=4、Super=8。 */
    private const int ModifierParameterBase = 1;
    private const int ModifierBitShift = 1;
    private const int ModifierBitAlt = 2;
    private const int ModifierBitControl = 4;

    /** modifyOtherKeys 的键码前缀: `ESC [ 27 ; 修饰 ; codepoint ~`。 */
    private const int ModifyOtherKeysCode = 27;

    /** CSI-u 事件类型: 1 按下、2 重复、3 释放。 */
    private const int KeyEventPress = 1;

    /** 认不出的键: 键名与字符同时为空, `TryParse` 据此丢弃整条序列(超出 BMP 的码点也走这里)。 */
    private static readonly ConsoleKeyInfo NoKey = new('\0', ConsoleKey.NoName, false, false, false);

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
        var final = (char)_pending[end];
        input = TerminalInputEvent.FromKey(CsiKey(final, parameters));
        if (input.Key.Key == ConsoleKey.NoName && input.Key.KeyChar == '\0')
            Trace.TraceInformation($"unhandled CSI sequence: final='{final}' (0x{(int)final:x2}), parameters=\"{EscapeForLog(parameters)}\"");
        consumed = end + 1;
        return true;
    }

    /** 日志里的不可打印字节转成 `\xNN`, 免得原始字节把日志行搅乱。 */
    private static string EscapeForLog(string parameters)
        => string.Concat(parameters.Select(value => value is >= ' ' and < '\u007f'
            ? value.ToString()
            : $"\\x{(int)value:x2}"));

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
        'P' => new ConsoleKeyInfo('\0', ConsoleKey.F1, false, false, false),
        'Q' => new ConsoleKeyInfo('\0', ConsoleKey.F2, false, false, false),
        'R' => new ConsoleKeyInfo('\0', ConsoleKey.F3, false, false, false),
        'S' => new ConsoleKeyInfo('\0', ConsoleKey.F4, false, false, false),
        _ => default,
    };

    private static ConsoleKeyInfo CsiKey(char final, string parameters)
    {
        if (final == 'u')
            return CsiUKey(parameters);
        if (final == '~' && TryModifyOtherKeys(parameters, out var modified))
            return modified;

        var key = final switch
        {
            'A' => ConsoleKey.UpArrow,
            'B' => ConsoleKey.DownArrow,
            'C' => ConsoleKey.RightArrow,
            'D' => ConsoleKey.LeftArrow,
            'H' => ConsoleKey.Home,
            'F' => ConsoleKey.End,
            // xterm 修饰键形式 `ESC [ n ; m ~` 只看键码 n, 否则 Ctrl/Shift+Delete 之类会认不出键。
            '~' => parameters.Split(';')[0] switch
            {
                "1" or "7" => ConsoleKey.Home,
                "2" => ConsoleKey.Insert,
                "3" => ConsoleKey.Delete,
                "4" or "8" => ConsoleKey.End,
                "5" => ConsoleKey.PageUp,
                "6" => ConsoleKey.PageDown,
                "11" => ConsoleKey.F1,
                "12" => ConsoleKey.F2,
                "13" => ConsoleKey.F3,
                "14" => ConsoleKey.F4,
                "15" => ConsoleKey.F5,
                "17" => ConsoleKey.F6,
                "18" => ConsoleKey.F7,
                "19" => ConsoleKey.F8,
                "20" => ConsoleKey.F9,
                "21" => ConsoleKey.F10,
                "23" => ConsoleKey.F11,
                "24" => ConsoleKey.F12,
                _ => ConsoleKey.NoName,
            },
            _ => ConsoleKey.NoName,
        };
        return new ConsoleKeyInfo('\0', key, false, false, false);
    }

    /**
     * kitty 键盘协议的 CSI-u 形式: `ESC [ codepoint[;修饰[:事件]] u`。
     * 只有按下事件产生输入, 重复/释放(`codepoint;3:3u`、`codepoint;3:2u`)与组合码点一律不产出键。
     */
    private static ConsoleKeyInfo CsiUKey(string parameters)
    {
        var fields = parameters.Split(';');
        if (fields.Length > 2 || !int.TryParse(fields[0].Split(':')[0], out var codePoint))
            return NoKey;
        if (fields.Length == 1)
            return CodePointKey(codePoint, ConsoleModifiers.None);

        var modifiers = fields[1].Split(':');
        if (!int.TryParse(modifiers[0], out var modifierParameter))
            return NoKey;
        if (modifiers.Length > 1 && (!int.TryParse(modifiers[1], out var eventType) || eventType != KeyEventPress))
            return NoKey;
        return CodePointKey(codePoint, ModifiersOf(modifierParameter));
    }

    /** `ESC [ 27 ; 修饰 ; codepoint ~`: 参数不是这一形状时返回 false, 交回普通键码表。 */
    private static bool TryModifyOtherKeys(string parameters, out ConsoleKeyInfo key)
    {
        key = default;
        var parts = parameters.Split(';');
        if (parts.Length != 3
            || !int.TryParse(parts[0], out var code)
            || code != ModifyOtherKeysCode
            || !int.TryParse(parts[1], out var modifierParameter)
            || !int.TryParse(parts[2], out var codePoint))
            return false;
        key = CodePointKey(codePoint, ModifiersOf(modifierParameter));
        return true;
    }

    private static ConsoleModifiers ModifiersOf(int modifierParameter)
    {
        var bits = modifierParameter - ModifierParameterBase;
        var modifiers = ConsoleModifiers.None;
        if ((bits & ModifierBitShift) != 0)
            modifiers |= ConsoleModifiers.Shift;
        if ((bits & ModifierBitAlt) != 0)
            modifiers |= ConsoleModifiers.Alt;
        if ((bits & ModifierBitControl) != 0)
            modifiers |= ConsoleModifiers.Control;
        return modifiers;
    }

    /** codepoint + 修饰键 → 按键; 超出单个 UTF-16 码元或本身不可映射(代理区/控制码)时返回 NoKey。 */
    private static ConsoleKeyInfo CodePointKey(int codePoint, ConsoleModifiers modifiers)
    {
        var shift = (modifiers & ConsoleModifiers.Shift) != 0;
        var alt = (modifiers & ConsoleModifiers.Alt) != 0;
        var control = (modifiers & ConsoleModifiers.Control) != 0;
        if (codePoint is 8 or 0x7f)
            return new ConsoleKeyInfo('\0', ConsoleKey.Backspace, shift, alt, control);
        if (codePoint == 0x09)
            return new ConsoleKeyInfo('\t', ConsoleKey.Tab, shift, alt, control);
        if (codePoint is 0x0a or 0x0d)
            return new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift, alt, control);
        if (codePoint == 0x1b)
            return new ConsoleKeyInfo('\0', ConsoleKey.Escape, shift, alt, control);
        if (codePoint is < 0x20 or > 0xffff or >= 0xd800 and <= 0xdfff)
            return NoKey;

        var character = (char)codePoint;
        if (character == ' ')
            return new ConsoleKeyInfo(character, ConsoleKey.Spacebar, shift, alt, control);
        // Ctrl+字母不带字符, 与裸控制字节(0x01..0x1a)的既有处理保持一致。
        if (character is >= 'a' and <= 'z')
            return new ConsoleKeyInfo(control ? '\0' : character, ConsoleKey.A + (character - 'a'), shift, alt, control);
        if (character is >= 'A' and <= 'Z')
            return new ConsoleKeyInfo(control ? '\0' : character, ConsoleKey.A + (character - 'A'), shift, alt, control);
        return new ConsoleKeyInfo(character, ConsoleKey.NoName, shift, alt, control);
    }
}

/** 阻塞读取原始字节并逐个解码为键或鼠标事件; 仅在 Unix 终端后端使用。自管道承载 UI 重绘的跨线程唤醒。 */
internal sealed class TerminalInputReader : ITerminalInputSource
{
    private const short PollIn = 0x0001;
    private const int InterruptedSystemCall = 4;
    private const int FGetFl = 3;
    private const int FSetFl = 4;

    /** O_NONBLOCK 随平台不同: Linux 0x800, Darwin/FreeBSD 0x4(Darwin 的 0x800 是 O_EXCL, F_SETFL 会忽略它, 管道仍是阻塞的)。 */
    private static readonly int ONonBlock = OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD() ? 0x4 : 0x800;

    /** Darwin arm64 把变参放在栈上, fcntl 的第三个参数要排在八个寄存器参数之后。 */
    private static readonly bool VariadicArgumentsOnStack = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

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
        SetNonBlocking(_wakeRead);
        SetNonBlocking(_wakeWrite);
    }

    /**
     * DrainWake 读到管道为空才返回, 所以两端必须非阻塞。
     * macOS 上曾用 Linux 的 O_NONBLOCK 值且按寄存器传变参, 管道留在阻塞模式, 第一次 Wake 后输入线程永久卡在 read 里。
     */
    private static void SetNonBlocking(int fd)
    {
        var result = VariadicArgumentsOnStack
            ? fcntlAfterRegisters(fd, FSetFl, 0, 0, 0, 0, 0, 0, ONonBlock)
            : fcntl(fd, FSetFl, ONonBlock);
        if (result != 0)
            throw new InvalidOperationException($"fcntl(F_SETFL, O_NONBLOCK) failed, errno={Marshal.GetLastWin32Error()}");
    }

    public bool EndOfStream { get; private set; }

    /** 内核视角的唤醒管道状态: 两端都带 O_NONBLOCK 时, 排空管道读到空会立即返回而不是阻塞。 */
    internal bool WakePipeIsNonBlocking
        => (fcntl(_wakeRead, FGetFl, 0) & ONonBlock) != 0 && (fcntl(_wakeWrite, FGetFl, 0) & ONonBlock) != 0;

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
                // resize/子进程退出信号只中断系统调用, 不是输入流结束。
                if (Marshal.GetLastWin32Error() == InterruptedSystemCall)
                    continue;
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
            if (read < 0 && Marshal.GetLastWin32Error() == InterruptedSystemCall)
                continue;
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

    /** 同一个 fcntl, 用六个占位寄存器参数把 arg 推到第一个栈槽: Darwin arm64 从那里读取变参。 */
    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int fcntlAfterRegisters(int fd, int cmd, nint x2, nint x3, nint x4, nint x5, nint x6, nint x7, nint arg);

    [DllImport("libc", SetLastError = true)]
    private static extern int write(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
