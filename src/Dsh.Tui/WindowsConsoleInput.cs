using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Dsh.Tui;

/**
 * Windows 控制台输入记录读取器: 直接用 ReadConsoleInputW 取 MOUSE_EVENT, 不经 SGR 转义序列。
 * ConPTY 的鼠标转义序列翻译受 Windows build 限制, 控制台输入记录是 crossterm/psmux 采用的本地路径。
 * 控制台输入句柄可等待, 与唤醒事件一起 WaitAny: 支持超时(trailing 渲染窗口)与 UI 事件跨线程唤醒。
 */
internal sealed class WindowsConsoleInputReader : ITerminalInputSource
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
    private readonly nint _handle;
    private readonly ManualResetEvent _consoleReady;
    private readonly ManualResetEvent _wake = new(false);
    private readonly WaitHandle[] _waitHandles;

    private WindowsConsoleInputReader(nint handle)
    {
        _handle = handle;
        _consoleReady = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false) };
        _waitHandles = [_consoleReady, _wake];
    }

    public bool EndOfStream { get; private set; }

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

    public TerminalInputEvent? Read(int timeoutMs)
    {
        while (true)
        {
            if (_ready.Count > 0)
                return _ready.Dequeue();
            var signaled = WaitHandle.WaitAny(_waitHandles, timeoutMs);
            if (signaled == WaitHandle.WaitTimeout)
                return null;
            if (signaled == 1)
            {
                _wake.Reset();
                return null;
            }
            if (!ReadConsoleInputW(_handle, _records, (uint)_records.Length, out var count) || count == 0)
            {
                EndOfStream = true;
                return null;
            }
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

    public void Wake() => _wake.Set();

    public void Dispose()
    {
        _wake.Dispose();
        _consoleReady.Dispose();
    }

    /** VK 码与 ConsoleKey 取值一致(方向键/功能键/字母数字), 因此可直接转换; 纯修饰键只更新修饰状态, 不作为按键上报。 */
    internal static ConsoleKeyInfo? MapKey(ushort virtualKeyCode, char unicodeChar, uint controlKeyState, bool keyDown)
    {
        if (!keyDown)
            return null;
        // Shift/Ctrl/Alt 自身的按下记录不带字符, 修饰状态已由 ControlKeyState 给出。
        // 放行会造成真实缺陷: 按 Shift+'+' 时宿主(ConPTY/真实键盘)先送一条 Shift 按下, 被上层当按键消费。
        if (virtualKeyCode is VirtualKeyShift or VirtualKeyControl or VirtualKeyMenu)
            return null;
        var key = virtualKeyCode == 0 ? ConsoleKey.NoName : (ConsoleKey)virtualKeyCode;
        var shift = (controlKeyState & ShiftPressed) != 0;
        var alt = (controlKeyState & (LeftAltPressed | RightAltPressed)) != 0;
        var control = (controlKeyState & (LeftCtrlPressed | RightCtrlPressed)) != 0;
        // 宿主只给 VK 不给字符时(Shift 类符号如 $ ( )、AltGr 组合)按当前键盘布局补出字符, 否则这些键会被静默丢弃。
        if (unicodeChar == '\0' && IsPrintableKey(virtualKeyCode))
            unicodeChar = ToCharacter(virtualKeyCode, controlKeyState);
        return new ConsoleKeyInfo(unicodeChar, key, shift, alt, control);
    }

    /** 可产生字符的键: 字母数字、OEM 符号、小键盘数字(修饰键与功能键不算)。 */
    private static bool IsPrintableKey(ushort virtualKeyCode)
        => virtualKeyCode is >= 0x30 and <= 0x39
            or >= 0x41 and <= 0x5A
            or >= 0x60 and <= 0x6F
            or >= 0xBA and <= 0xE2;

    /** 用当前键盘布局把 VK+修饰状态翻成字符(ToUnicodeEx 尊重用户实际布局与 AltGr)。 */
    private static char ToCharacter(ushort virtualKeyCode, uint controlKeyState)
    {
        Span<byte> keyboardState = stackalloc byte[256];
        if ((controlKeyState & ShiftPressed) != 0)
            keyboardState[0x10] = 0x80;
        if ((controlKeyState & (LeftCtrlPressed | RightCtrlPressed)) != 0)
            keyboardState[0x11] = 0x80;
        if ((controlKeyState & (LeftAltPressed | RightAltPressed)) != 0)
            keyboardState[0x12] = 0x80;
        Span<char> buffer = stackalloc char[4];
        var count = ToUnicodeEx(virtualKeyCode, 0, keyboardState, buffer, buffer.Length, 0, GetKeyboardLayout(0));
        return count > 0 ? buffer[0] : '\0';
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
        {
            // 放行移动: 拖动选择需要它; 无键移动(button 3)也放行, 渲染侧有 10ms 合并
            var moved = (buttonState & LeftButtonPressed) != 0 ? 0
                : (buttonState & MiddleButtonPressed) != 0 ? 1
                : (buttonState & RightButtonPressed) != 0 ? 2
                : 3;
            return new TerminalMouseEvent(x, y, moved, moved != 3, IsMove: true);
        }
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ToUnicodeEx(ushort virtualKey, uint scanCode, Span<byte> keyState, Span<char> buffer, int bufferSize, uint flags, nint keyboardLayout);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetKeyboardLayout(uint threadId);
}
