using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Dsh.Tui;

public sealed class TerminalRawMode : IDisposable
{
    private const int TcsaNow = 0;
    private const int LinuxTermiosSize = 60;
    private const int MacTermiosSize = 72;
    private const int LinuxCcStart = 17;
    private const int MacCcStart = 32;
    private const int LinuxVMin = 6;
    private const int LinuxVTime = 5;
    private const int MacVMin = 16;
    private const int MacVTime = 17;
    private const uint LinuxIcrnl = 0x0100;
    private const uint LinuxIxon = 0x0400;
    private const uint LinuxIcanon = 0x0002;
    private const uint LinuxEcho = 0x0008;
    private const uint LinuxIsig = 0x0001;
    private const uint LinuxIexten = 0x8000;
    private const uint LinuxOpost = 0x0001;
    private const ulong MacIcrnl = 0x00000100;
    private const ulong MacIxon = 0x00000200;
    private const ulong MacIcanon = 0x00000100;
    private const ulong MacEcho = 0x00000008;
    private const ulong MacIsig = 0x00000080;
    private const ulong MacIexten = 0x00000400;
    private const ulong MacOpost = 0x00000001;

    /** ?1002 = 拖动(button-event)跟踪, 文本选择需要; ?1006 = SGR 扩展坐标。 */
    internal const string MouseEnableSequence = "\x1b[?1000h\x1b[?1002h\x1b[?1006h";

    /** 关闭时把被桥接/常驻进程可能开过的 ?1003(全量移动)/?1015(urxvt) 一并复位, 否则退出后终端仍刷鼠标上报。 */
    internal const string MouseDisableSequence = "\x1b[?1006l\x1b[?1003l\x1b[?1002l\x1b[?1000l\x1b[?1015l";

    /** 括号粘贴: 开启后终端把粘贴内容包在 ESC[200~ … ESC[201~ 里, 使多字符粘贴原子到达(图片路径路线)。 */
    internal const string BracketedPasteEnableSequence = "\x1b[?2004h";

    internal const string BracketedPasteDisableSequence = "\x1b[?2004l";

    /** 备用屏幕接管: 进 ?1049h + 藏光标 ?25l; 退出反向恢复, 主屏现场(shell 提示符/滚动历史)原样奉还。 */
    internal const string ScreenEnterSequence = "\x1b[?1049h\x1b[?25l";

    internal const string ScreenExitSequence = "\x1b[?25h\x1b[?1049l";

    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const int StdInputHandle = -10;

    private readonly bool _active;
    private readonly byte[]? _original;
    private readonly bool _restoreTreatControlCAsInput;
    private readonly bool _mouseEnabled;
    private readonly nint _consoleInputHandle;
    private readonly uint? _consoleInputMode;
    private bool _disposed;

    private static readonly object ActiveGate = new();
    private static readonly List<WeakReference<TerminalRawMode>> Active = [];
    private static bool _restoreHooksRegistered;

    private TerminalRawMode(
        bool active,
        byte[]? original = null,
        bool restoreTreatControlCAsInput = false,
        bool mouseEnabled = false,
        nint consoleInputHandle = 0,
        uint? consoleInputMode = null)
    {
        _active = active;
        _original = original;
        _restoreTreatControlCAsInput = restoreTreatControlCAsInput;
        _mouseEnabled = mouseEnabled;
        _consoleInputHandle = consoleInputHandle;
        _consoleInputMode = consoleInputMode;
    }

    internal static string MouseEnableSequenceForTests => MouseEnableSequence;

    internal static string MouseDisableSequenceForTests => MouseDisableSequence;

    private static TerminalRawMode Track(TerminalRawMode mode)
    {
        RegisterForRestore(mode);
        return mode;
    }

    /** 崩溃兜底: 未处理异常/进程退出时也要把终端恢复(Dispose 幂等), 不给宿主留 raw+备用屏幕残骸。 */
    private static void RegisterForRestore(TerminalRawMode mode)
    {
        lock (ActiveGate)
        {
            Active.Add(new WeakReference<TerminalRawMode>(mode));
            if (_restoreHooksRegistered)
                return;
            _restoreHooksRegistered = true;
            AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreActive();
            AppDomain.CurrentDomain.UnhandledException += (_, _) => RestoreActive();
        }
    }

    private static void RestoreActive()
    {
        List<TerminalRawMode> live;
        lock (ActiveGate)
        {
            live = [];
            foreach (var reference in Active)
                if (reference.TryGetTarget(out var mode))
                    live.Add(mode);
        }
        foreach (var mode in live)
            mode.Dispose();
    }

    public static TerminalRawMode? TryEnable(bool enableMouse = false, bool emitMouseReports = true)
    {
        if (OperatingSystem.IsWindows())
            return TryEnableWindows(enableMouse, emitMouseReports);
        return TryEnableUnix(enableMouse, emitMouseReports);
    }

    /**
     * 鼠标上报开关序列: 只有拥有用户终端的进程才应发出。常驻会话的终端是 daemon 的 ConPTY,
     * 发出会经 daemon 泄到用户终端、与 proxy 重复, 因此由 emitMouseReports 单独控制。
     */
    internal static string MouseReportEnableSequenceFor(bool enableMouse, bool emitMouseReports)
        => enableMouse && emitMouseReports ? MouseEnableSequence : "";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_active)
            return;

        // 先恢复终端可见状态(鼠标/备用屏幕/光标), 再恢复输入模式
        WriteSequence((_mouseEnabled ? MouseDisableSequence : "") + BracketedPasteDisableSequence + ScreenExitSequence);
        if (_original is not null)
        {
            var buffer = Marshal.AllocHGlobal(_original.Length);
            try
            {
                Marshal.Copy(_original, 0, buffer, _original.Length);
                tcsetattr(0, TcsaNow, buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return;
        }

        try
        {
            if (_consoleInputMode is { } mode && _consoleInputHandle != 0)
                SetConsoleMode(_consoleInputHandle, mode);
            else
                Console.TreatControlCAsInput = _restoreTreatControlCAsInput;
            Console.CursorVisible = true;
        }
        catch (IOException)
        {
        }
    }

    internal static int LinuxTermiosSizeForTests => LinuxTermiosSize;

    internal static int LinuxVMinOffsetForTests => LinuxCcStart + LinuxVMin;

    internal static int LinuxVTimeOffsetForTests => LinuxCcStart + LinuxVTime;

    internal static bool TryProbeLinuxTermios()
    {
        var buffer = Marshal.AllocHGlobal(LinuxTermiosSize);
        try
        {
            return tcgetattr(0, buffer) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /**
     * Windows 原始模式: 关掉行/回显/整行处理与快速编辑, 开鼠标输入。快速编辑开着时鼠标点击会被控制台
     * 截去做选择而不进输入缓冲; 清除虚拟终端输入位以保证键盘按虚拟键码送达(与 psmux 的本地路径一致)。
     * 句柄或模式不可写时退回仅 TreatControlCAsInput, 此时输入源会退回 Console.ReadKey。
     */
    private static TerminalRawMode? TryEnableWindows(bool enableMouse, bool emitMouseReports)
    {
        // 只有本进程同时拥有输入与输出终端时才接管: 否则(stdout 被重定向到管道)我们并不"拥有"这个控制台,
        // 对它做 SetConsoleMode/鼠标上报会落到别的进程(宿主)的控制台上。
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return null;

        try
        {
            var restoreTreatControlCAsInput = Console.TreatControlCAsInput;
            Console.CursorVisible = false;
            var handle = GetStdHandle(StdInputHandle);
            if (handle == 0 || handle == -1 || !GetConsoleMode(handle, out var original))
            {
                WriteSequence(BracketedPasteEnableSequence + ScreenEnterSequence);
                return Track(new TerminalRawMode(true, restoreTreatControlCAsInput: restoreTreatControlCAsInput));
            }
            var mode = (original
                & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput | EnableQuickEditMode | EnableVirtualTerminalInput))
                | EnableExtendedFlags;
            if (enableMouse)
                mode |= EnableMouseInput;
            if (!SetConsoleMode(handle, mode))
            {
                WriteSequence(BracketedPasteEnableSequence + ScreenEnterSequence);
                return Track(new TerminalRawMode(true, restoreTreatControlCAsInput: restoreTreatControlCAsInput));
            }
            // 丢掉接管前排队的陈旧输入(可能是残留鼠标跟踪留下的 X10 字节)
            FlushConsoleInputBuffer(handle);
            WriteSequence(MouseReportEnableSequenceFor(enableMouse, emitMouseReports) + BracketedPasteEnableSequence + ScreenEnterSequence);
            return Track(new TerminalRawMode(
                true,
                restoreTreatControlCAsInput: restoreTreatControlCAsInput,
                mouseEnabled: enableMouse && emitMouseReports,
                consoleInputHandle: handle,
                consoleInputMode: original));
        }
        catch (IOException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private static TerminalRawMode? TryEnableUnix(bool enableMouse, bool emitMouseReports)
    {
        if (Console.IsOutputRedirected)
            return null;
        var size = OperatingSystem.IsMacOS() ? MacTermiosSize : LinuxTermiosSize;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (tcgetattr(0, buffer) != 0)
                return null;
            var original = new byte[size];
            Marshal.Copy(buffer, original, 0, size);
            var raw = new byte[size];
            Array.Copy(original, raw, size);
            ApplyRaw(raw, size);
            Marshal.Copy(raw, 0, buffer, size);
            if (tcsetattr(0, TcsaNow, buffer) != 0)
                return null;
            WriteSequence(MouseReportEnableSequenceFor(enableMouse, emitMouseReports) + BracketedPasteEnableSequence + ScreenEnterSequence);
            return Track(new TerminalRawMode(true, original, mouseEnabled: enableMouse && emitMouseReports));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void WriteSequence(string sequence)
    {
        try
        {
            if (Console.IsOutputRedirected)
                return;
            Console.Out.Write(sequence);
            Console.Out.Flush();
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static void ApplyRaw(byte[] buffer, int size)
    {
        if (OperatingSystem.IsMacOS())
        {
            var iflagOffset = 0;
            var oflagOffset = 8;
            var lflagOffset = 24;
            var c0 = MacCcStart;
            SetUInt64(buffer, iflagOffset, GetUInt64(buffer, iflagOffset) & ~(MacIcrnl | MacIxon));
            SetUInt64(buffer, oflagOffset, GetUInt64(buffer, oflagOffset) & ~MacOpost);
            SetUInt64(buffer, lflagOffset, GetUInt64(buffer, lflagOffset) & ~(MacIcanon | MacEcho | MacIsig | MacIexten));
            if (c0 + MacVMin < size)
                buffer[c0 + MacVMin] = 1;
            if (c0 + MacVTime < size)
                buffer[c0 + MacVTime] = 0;
            return;
        }

        SetUInt32(buffer, 0, GetUInt32(buffer, 0) & ~(LinuxIcrnl | LinuxIxon));
        SetUInt32(buffer, 4, GetUInt32(buffer, 4) & ~LinuxOpost);
        SetUInt32(buffer, 12, GetUInt32(buffer, 12) & ~(LinuxIcanon | LinuxEcho | LinuxIsig | LinuxIexten));
        if (LinuxCcStart + LinuxVMin < size)
            buffer[LinuxCcStart + LinuxVMin] = 1;
        if (LinuxCcStart + LinuxVTime < size)
            buffer[LinuxCcStart + LinuxVTime] = 0;
    }

    private static uint GetUInt32(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)));

    private static void SetUInt32(byte[] buffer, int offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, sizeof(uint)), value);

    private static ulong GetUInt64(byte[] buffer, int offset)
        => BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)));

    private static void SetUInt64(byte[] buffer, int offset, ulong value)
        => BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, sizeof(ulong)), value);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcgetattr(int fd, IntPtr termios);

    [DllImport("libc", SetLastError = true)]
    private static extern int tcsetattr(int fd, int optionalActions, IntPtr termios);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushConsoleInputBuffer(nint hConsoleInput);
}