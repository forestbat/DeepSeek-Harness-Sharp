using System.Runtime.InteropServices;

namespace Dsh.Pty;

/**
 * 把鼠标事件以原生 MOUSE_EVENT 记录写进目标进程的控制台输入缓冲。
 *
 * 不能写 pty: ConPTY 不把鼠标序列翻译成记录, 记录读取型子进程(用 ReadConsoleInputW 的 TUI)只会看到字面字符
 * —— psmux issue #98 的结论, 也是我们输入行出现 `@4;` 的成因。
 * 三步(psmux 同款): FreeConsole → AttachConsole(child_pid) → CreateFileW("CONIN$") → WriteConsoleInputW。
 * 现代 Windows 上句柄在 FreeConsole 之后仍然有效(真内核句柄)。
 */
internal static class WindowsConsoleMouseInjector
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const ushort MouseEventType = 0x0002;
    private static readonly nint InvalidHandle = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEventRecord
    {
        public Coord Position;
        public uint ButtonState;
        public uint ControlKeyState;
        public uint EventFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputRecord
    {
        public ushort EventType;
        public ushort Padding;
        public MouseEventRecord Mouse;
    }

    public static void Inject(int processId, PtyMouseEvent mouse)
    {
        if (!TryOpen(processId, out var handle))
            return;

        try
        {
            var record = new InputRecord
            {
                EventType = MouseEventType,
                Padding = 0,
                Mouse = new MouseEventRecord
                {
                    Position = new Coord { X = mouse.X, Y = mouse.Y },
                    ButtonState = mouse.ButtonState,
                    ControlKeyState = 0,
                    EventFlags = mouse.EventFlags,
                },
            };
            WriteConsoleInputW(handle, [record], 1, out _);
        }
        finally
        {
            CloseHandle(handle);
            FreeConsole();
        }
    }

    private static bool TryOpen(int processId, out nint handle)
    {
        FreeConsole();
        handle = 0;
        if (!AttachConsole((uint)processId))
            return false;

        handle = CreateFileW("CONIN$", GenericRead | GenericWrite, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (handle != InvalidHandle && handle != 0)
            return true;

        FreeConsole();
        handle = 0;
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        nint lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    // 注意: 这里不能标 [Out] —— 那会让封送层只准备一块清零的缓冲区, 写进去的记录全是 0。
    private static extern bool WriteConsoleInputW(nint hConsoleInput, InputRecord[] lpBuffer, uint nLength, out uint lpNumberOfEventsWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
