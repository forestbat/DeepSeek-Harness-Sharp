using System.Runtime.InteropServices;

namespace Dsh.Pty;

/**
 * Win32 控制台输入记录的共享布局与映射: 常驻会话(读自己的控制台)与终端代理(读宿主终端)用同一份,
 * 避免两处各写一遍 VK/位标志/结构体布局。
 *
 * 鼠标记录按 psmux 的做法**原样带上**(buttonState/eventFlags/坐标), 由 daemon 注入常驻会话控制台,
 * 不走"写进 pty 的 VT 字节"——ConPTY 不会把鼠标转义序列翻成鼠标记录, 那只会变成子进程里的乱码文本。
 */
public static class WindowsConsoleRecord
{
    public const ushort KeyEventType = 0x0001;
    public const ushort MouseEventType = 0x0002;

    public const int StdInputHandle = -10;

    // ControlKeyState 位
    public const uint RightAltPressed = 0x0001;
    public const uint LeftAltPressed = 0x0002;
    public const uint RightCtrlPressed = 0x0004;
    public const uint LeftCtrlPressed = 0x0008;
    public const uint ShiftPressed = 0x0010;

    // 鼠标 dwButtonState 位
    public const uint LeftButtonPressed = 0x0001;
    public const uint RightButtonPressed = 0x0002;
    public const uint MiddleButtonPressed = 0x0004;

    // 鼠标 dwEventFlags
    public const uint MouseMoved = 0x0001;
    public const uint MouseWheeled = 0x0004;

    private const ushort VirtualKeyShift = 0x10;
    private const ushort VirtualKeyControl = 0x11;
    private const ushort VirtualKeyMenu = 0x12;

    /**
     * VK 码与 ConsoleKey 取值一致(方向键/功能键/字母数字), 因此可直接转换; 纯修饰键只更新修饰状态, 不作为按键上报。
     */
    public static ConsoleKeyInfo? MapKey(ushort virtualKeyCode, char unicodeChar, uint controlKeyState, bool keyDown)
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

    /** 控制台句柄是否可读(是真控制台或伪控制台; 管道句柄会失败)。 */
    public static bool TryGetConsoleMode(nint handle, out uint mode) => GetConsoleMode(handle, out mode);

    public static nint InputHandle() => GetStdHandle(StdInputHandle);

    /** 以下字段布局对应 Win32 COORD / KEY_EVENT_RECORD / MOUSE_EVENT_RECORD / INPUT_RECORD。 */
    [StructLayout(LayoutKind.Sequential)]
    public struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyEventRecord
    {
        public int KeyDown;
        public ushort RepeatCount;
        public ushort VirtualKeyCode;
        public ushort VirtualScanCode;
        public char UnicodeChar;
        public uint ControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseEventRecord
    {
        public Coord MousePosition;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct InputRecord
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
    public static extern bool ReadConsoleInputW(nint hConsoleInput, [Out] InputRecord[] lpBuffer, uint nLength, out uint lpNumberOfEventsRead);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int ToUnicodeEx(ushort virtualKey, uint scanCode, Span<byte> keyState, Span<char> buffer, int bufferSize, uint flags, nint keyboardLayout);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetKeyboardLayout(uint threadId);
}
