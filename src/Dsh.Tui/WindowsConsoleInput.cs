using System.Runtime.InteropServices;

namespace Dsh.Tui;

/**
 * Windows 控制台输入记录读取器: 直接用 ReadConsoleInputW 取 MOUSE_EVENT, 不经 SGR 转义序列。
 * ConPTY 的鼠标转义序列翻译受 Windows build 限制, 控制台输入记录是 crossterm/psmux 采用的本地路径。
 */
internal sealed class WindowsConsoleInputReader(nint handle) : ITerminalInputSource
{
    private const int StdInputHandle = -10;
    private const ushort KeyEventType = 0x0001;
    private const ushort MouseEventType = 0x0002;
    private const uint ShiftPressed = 0x0010;
    private const uint LeftAltPressed = 0x0002;
    private const uint RightAltPressed = 0x0001;
    private const uint LeftCtrlPressed = 0x0008;
    private const uint RightCtrlPressed = 0x0004;
    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyMenu = 0x12;
    private const int WheelUpButton = 64;
    private const int WheelDownButton = 65;
    private const uint LeftButtonPressed = 0x0001;
    private const uint RightButtonPressed = 0x0002;
    private const uint MiddleButtonPressed = 0x0004;
    private const uint MouseMoved = 0x0001;
    private const uint MouseWheeled = 0x0004;

    private readonly InputRecord[] _records = new InputRecord[64];

    private readonly Queue<TerminalInputEvent> _ready = new();

    /** 控制台句柄不可用时返回 null, 由调用方回退到 Console.ReadKey。 */
    public static WindowsConsoleInputReader? TryCreate()
    {
        if (Console.IsInputRedirected)
            return null;
        var handle = GetStdHandle(StdInputHandle);
        if (handle == 0 || handle == -1 || !GetConsoleMode(handle, out _))
            return null;
        return new WindowsConsoleInputReader(handle);
    }

    public TerminalInputEvent? Read()
    {
        while (true)
        {
            if (_ready.Count > 0)
                return _ready.Dequeue();
            if (!ReadConsoleInputW(handle, _records, (uint)_records.Length, out var count) || count == 0)
                return null;
            for (var index = 0; index < count; index++)
            {
                var record = _records[index];
                if (record.EventType == KeyEventType
                    && MapKey(
                        record.KeyEvent.VirtualKeyCode,
                        record.KeyEvent.UnicodeChar,
                        record.KeyEvent.ControlKeyState,
                        record.KeyEvent.KeyDown != 0) is { } key)
                    _ready.Enqueue(TerminalInputEvent.FromKey(key));
                else if (record.EventType == MouseEventType
                    && MapMouse(
                        record.MouseEvent.MousePosition.X,
                        record.MouseEvent.MousePosition.Y,
                        record.MouseEvent.ButtonState,
                        record.MouseEvent.EventFlags) is { } mouse)
                    _ready.Enqueue(TerminalInputEvent.FromMouse(mouse));
            }
        }
    }

    public void Dispose()
    {
    }

    /** VK 码与 ConsoleKey 取值一致(方向键/功能键/字母数字), 因此可直接转换。 */
    internal static ConsoleKeyInfo? MapKey(ushort virtualKeyCode, char unicodeChar, uint controlKeyState, bool keyDown)
    {
        if (!keyDown)
            return null;
        var key = virtualKeyCode == 0 || virtualKeyCode is VirtualKeyShift or VirtualKeyControl or VirtualKeyMenu
            ? ConsoleKey.NoName
            : (ConsoleKey)virtualKeyCode;
        var shift = (controlKeyState & ShiftPressed) != 0;
        var alt = (controlKeyState & (LeftAltPressed | RightAltPressed)) != 0;
        var control = (controlKeyState & (LeftCtrlPressed | RightCtrlPressed)) != 0;
        return new ConsoleKeyInfo(unicodeChar, key, shift, alt, control);
    }

    /** 坐标已是 0 基; 按钮编号沿用 SGR 约定(0 左键 / 1 中键 / 2 右键 / 64/65 滚轮)。 */
    internal static TerminalMouseEvent? MapMouse(short x, short y, uint buttonState, uint eventFlags)
    {
        if ((eventFlags & MouseWheeled) != 0)
        {
            var delta = (short)(buttonState >> 16);
            return new TerminalMouseEvent(x, y, delta >= 0 ? WheelUpButton : WheelDownButton, true);
        }

        if ((eventFlags & MouseMoved) != 0)
            return null;
        if ((buttonState & LeftButtonPressed) != 0)
            return new TerminalMouseEvent(x, y, 0, true);
        if ((buttonState & MiddleButtonPressed) != 0)
            return new TerminalMouseEvent(x, y, 1, true);
        if ((buttonState & RightButtonPressed) != 0)
            return new TerminalMouseEvent(x, y, 2, true);
        return new TerminalMouseEvent(x, y, 0, false);
    }

    /** 以下字段布局对应 Win32 COORD / KEY_EVENT_RECORD / MOUSE_EVENT_RECORD / INPUT_RECORD。 */
    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEventRecord
    {
        public Coord MousePosition;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    private struct InputRecord
    {
        [FieldOffset(0)]
        public ushort EventType;

        [FieldOffset(4)]
        public KeyEventRecord KeyEvent;

        [FieldOffset(4)]
        public MouseEventRecord MouseEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadConsoleInputW(nint hConsoleInput, [Out] InputRecord[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);
}
