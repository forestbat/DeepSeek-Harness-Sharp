using Microsoft.Win32.SafeHandles;

namespace Dsh.Tui;

internal enum EvdevEventKind
{
    Key,
    Text,
    MouseMove,
    MouseButton,
    MouseWheel,
}

internal readonly record struct EvdevEvent(EvdevEventKind Kind, ConsoleKeyInfo Key, char Text, float X, float Y, bool Pressed, float Wheel);

/**
 * 裸 TTY 的第三个输入后端: 直读 /dev/input/event*(evdev), 产出与 GLFW 路径同形的键/文本/鼠标事件。
 * 键码按 Linux input-event-codes.h 映射; 鼠标只处理相对位移与左键, 绝对坐标设备留待后续。
 */
internal sealed class EvdevInput : IDisposable
{
    private const int InputEventSize = 24;
    private const ushort EvSyn = 0x00;
    private const ushort EvKey = 0x01;
    private const ushort EvRel = 0x02;
    private const ushort SynReport = 0;
    private const ushort RelX = 0;
    private const ushort RelY = 1;
    private const ushort RelWheel = 8;
    private const ushort BtnLeft = 0x110;
    private const ushort KeyLeftCtrl = 29;
    private const ushort KeyRightCtrl = 97;
    private const ushort KeyLeftShift = 42;
    private const ushort KeyRightShift = 54;
    private const ushort KeyLeftAlt = 56;
    private const ushort KeyRightAlt = 100;
    private const string ShiftedDigits = "!@#$%^&*()";

    private static readonly Dictionary<ushort, ConsoleKey> NamedKeys = new()
    {
        [28] = ConsoleKey.Enter,
        [1] = ConsoleKey.Escape,
        [14] = ConsoleKey.Backspace,
        [15] = ConsoleKey.Tab,
        [57] = ConsoleKey.Spacebar,
        [111] = ConsoleKey.Delete,
        [110] = ConsoleKey.Insert,
        [102] = ConsoleKey.Home,
        [107] = ConsoleKey.End,
        [104] = ConsoleKey.PageUp,
        [109] = ConsoleKey.PageDown,
        [103] = ConsoleKey.UpArrow,
        [108] = ConsoleKey.DownArrow,
        [105] = ConsoleKey.LeftArrow,
        [106] = ConsoleKey.RightArrow,
    };

    private static readonly Dictionary<ushort, char> LetterKeys = new()
    {
        [30] = 'a', [48] = 'b', [46] = 'c', [32] = 'd', [18] = 'e', [33] = 'f', [34] = 'g',
        [35] = 'h', [23] = 'i', [36] = 'j', [37] = 'k', [38] = 'l', [50] = 'm', [49] = 'n',
        [24] = 'o', [25] = 'p', [16] = 'q', [19] = 'r', [31] = 's', [20] = 't', [22] = 'u',
        [47] = 'v', [17] = 'w', [45] = 'x', [21] = 'y', [44] = 'z',
    };

    private static readonly Dictionary<ushort, char> DigitKeys = new()
    {
        [2] = '1', [3] = '2', [4] = '3', [5] = '4', [6] = '5',
        [7] = '6', [8] = '7', [9] = '8', [10] = '9', [11] = '0',
    };

    private readonly List<SafeFileHandle> _handles = [];
    private readonly List<int> _fds = [];
    private readonly Queue<EvdevEvent> _ready = new();
    private readonly byte[] _buffer = new byte[InputEventSize * 64];
    private readonly float _screenWidth;
    private readonly float _screenHeight;
    private float _mouseX;
    private float _mouseY;
    private int _deltaX;
    private int _deltaY;
    private bool _ctrl;
    private bool _shift;
    private bool _alt;
    private bool _disposed;

    public EvdevInput(float screenWidth, float screenHeight)
    {
        _screenWidth = Math.Max(1, screenWidth);
        _screenHeight = Math.Max(1, screenHeight);
        _mouseX = _screenWidth / 2;
        _mouseY = _screenHeight / 2;
        foreach (var path in EnumerateEventDevices())
            TryOpen(path);
    }

    public int DeviceCount => _fds.Count;

    /** 取一个事件: 队列空时最多等 timeoutMs 毫秒(0 为非阻塞)。 */
    public bool Read(int timeoutMs, out EvdevEvent evt)
    {
        while (_ready.Count == 0)
        {
            if (!Fill(timeoutMs))
            {
                evt = default;
                return false;
            }
        }
        evt = _ready.Dequeue();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var handle in _handles)
            handle.Dispose();
        _handles.Clear();
        _fds.Clear();
    }

    private static IEnumerable<string> EnumerateEventDevices()
    {
        if (!Directory.Exists("/dev/input"))
            return [];
        return Directory.EnumerateFiles("/dev/input", "event*").OrderBy(path => path, StringComparer.Ordinal);
    }

    private void TryOpen(string path)
    {
        try
        {
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _handles.Add(handle);
            _fds.Add((int)handle.DangerousGetHandle());
        }
        catch (Exception)
        {
            // 权限不足或设备不可用: 跳过该设备, 至少保留键盘所需的其他节点。
        }
    }

    private bool Fill(int timeoutMs)
    {
        if (_fds.Count == 0)
            return false;
        var fds = new PosixNative.PollFd[_fds.Count];
        for (var index = 0; index < _fds.Count; index++)
            fds[index] = new PosixNative.PollFd { Fd = _fds[index], Events = PosixNative.PollIn };
        if (PosixNative.poll(fds, fds.Length, timeoutMs) <= 0)
            return false;

        var added = false;
        for (var index = 0; index < fds.Length; index++)
        {
            if ((fds[index].Revents & (PosixNative.PollIn | PosixNative.PollErr | PosixNative.PollHup)) == 0)
                continue;
            var count = PosixNative.read(_fds[index], _buffer, _buffer.Length);
            for (var offset = 0; offset + InputEventSize <= count; offset += InputEventSize)
            {
                if (Parse(_buffer, offset))
                    added = true;
            }
        }
        return added;
    }

    private bool Parse(byte[] buffer, int offset)
    {
        var type = BitConverter.ToUInt16(buffer, offset + 16);
        var code = BitConverter.ToUInt16(buffer, offset + 18);
        var value = BitConverter.ToInt32(buffer, offset + 20);
        return type switch
        {
            EvSyn when code == SynReport => FlushMouse(),
            EvKey => ParseKey(code, value),
            EvRel => ParseRelative(code, value),
            _ => false,
        };
    }

    private bool ParseRelative(ushort code, int value)
    {
        switch (code)
        {
            case RelX:
                _deltaX += value;
                return false;
            case RelY:
                _deltaY += value;
                return false;
            case RelWheel when value != 0:
                _ready.Enqueue(new EvdevEvent(EvdevEventKind.MouseWheel, default, '\0', _mouseX, _mouseY, false, Math.Sign(value)));
                return true;
            default:
                return false;
        }
    }

    private bool FlushMouse()
    {
        if (_deltaX == 0 && _deltaY == 0)
            return false;
        _mouseX = Math.Clamp(_mouseX + _deltaX, 0, _screenWidth - 1);
        _mouseY = Math.Clamp(_mouseY + _deltaY, 0, _screenHeight - 1);
        _deltaX = 0;
        _deltaY = 0;
        _ready.Enqueue(new EvdevEvent(EvdevEventKind.MouseMove, default, '\0', _mouseX, _mouseY, false, 0));
        return true;
    }

    private bool ParseKey(ushort code, int value)
    {
        UpdateModifier(code, value);
        if (code == BtnLeft)
        {
            _ready.Enqueue(new EvdevEvent(EvdevEventKind.MouseButton, default, '\0', _mouseX, _mouseY, value != 0, 0));
            return true;
        }
        if (value == 0)
            return false;

        if (NamedKeys.TryGetValue(code, out var named))
        {
            _ready.Enqueue(KeyEvent(named));
            if (named == ConsoleKey.Spacebar && !_ctrl && !_alt)
                _ready.Enqueue(TextEvent(' '));
            return true;
        }
        if (LetterKeys.TryGetValue(code, out var letter))
        {
            _ready.Enqueue(KeyEvent(ConsoleKey.A + (letter - 'a')));
            if (!_ctrl && !_alt)
                _ready.Enqueue(TextEvent(_shift ? char.ToUpperInvariant(letter) : letter));
            return true;
        }
        if (DigitKeys.TryGetValue(code, out var digit))
        {
            _ready.Enqueue(KeyEvent(ConsoleKey.D0 + (digit - '0')));
            if (!_ctrl && !_alt)
                _ready.Enqueue(TextEvent(_shift ? ShiftedDigits[digit - '1'] : digit));
            return true;
        }
        return false;
    }

    private void UpdateModifier(ushort code, int value)
    {
        var down = value != 0;
        switch (code)
        {
            case KeyLeftCtrl or KeyRightCtrl:
                _ctrl = down;
                break;
            case KeyLeftShift or KeyRightShift:
                _shift = down;
                break;
            case KeyLeftAlt or KeyRightAlt:
                _alt = down;
                break;
        }
    }

    private EvdevEvent KeyEvent(ConsoleKey key)
        => new(EvdevEventKind.Key, new ConsoleKeyInfo('\0', key, _shift, _alt, _ctrl), '\0', 0, 0, false, 0);

    private static EvdevEvent TextEvent(char character)
        => new(EvdevEventKind.Text, new ConsoleKeyInfo(character, ConsoleKey.NoName, false, false, false), character, 0, 0, false, 0);
}
