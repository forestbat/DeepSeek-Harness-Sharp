using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Dsh.Pty;

public static class PtyConsoleBridge
{
    /** 被桥接进程可能开过鼠标上报(?1000/?1002/?1006)且仍在 daemon 中运行, detach 时替它复位宿主终端。 */
    internal const string MouseDisableSequence = "\x1b[?1006l\x1b[?1002l\x1b[?1000l";

    /** 请求宿主终端上报鼠标(按钮事件 + SGR 编码); 代理在 attach 时主动发出。 */
    internal const string MouseEnableSequence = "\x1b[?1000h\x1b[?1002h\x1b[?1006h";

    public static async Task RunAsync(PtySession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Attach();
        using var raw = PtyRawMode.TryEnable();
        try
        {
            var inputTask = PumpInputAsync(session, cancellationToken);
            var outputTask = PumpOutputAsync(session, cancellationToken);
            var exitTask = WaitForExitAsync(session, cancellationToken);
            await Task.WhenAny(inputTask, outputTask, exitTask);
            await session.StopAsync();
        }
        finally
        {
            session.Detach();
            if (!Console.IsOutputRedirected)
            {
                try
                {
                    await Console.Out.WriteAsync(MouseDisableSequence);
                    await Console.Out.FlushAsync();
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private static async Task PumpInputAsync(PtySession session, CancellationToken cancellationToken)
    {
        var stdin = Console.OpenStandardInput();
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stdin.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            await session.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task PumpOutputAsync(PtySession session, CancellationToken cancellationToken)
    {
        var stdout = Console.OpenStandardOutput();
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await session.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            await stdout.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            await stdout.FlushAsync(cancellationToken);
        }
    }

    private static async Task WaitForExitAsync(PtySession session, CancellationToken cancellationToken)
    {
        while (session.Status == PtySessionStatus.Running && !cancellationToken.IsCancellationRequested)
            await Task.Delay(100, cancellationToken);
    }
}

internal sealed class PtyRawMode : IDisposable
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

    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableMouseInput = 0x0010;

    private const uint EnableVirtualTerminalInput = 0x0200;
    private const int StdInputHandle = -10;

    private readonly bool _active;
    private readonly byte[]? _original;
    private readonly bool _restoreTreatControlCAsInput;
    private readonly nint _consoleInputHandle;
    private readonly uint? _consoleInputMode;
    private bool _disposed;

    private PtyRawMode(
        bool active,
        byte[]? original = null,
        bool restoreTreatControlCAsInput = false,
        nint consoleInputHandle = 0,
        uint? consoleInputMode = null)
    {
        _active = active;
        _original = original;
        _restoreTreatControlCAsInput = restoreTreatControlCAsInput;
        _consoleInputHandle = consoleInputHandle;
        _consoleInputMode = consoleInputMode;
    }

    public static PtyRawMode? TryEnable()
    {
        if (OperatingSystem.IsWindows())
            return TryEnableWindows();
        return TryEnableUnix();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_active)
            return;

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
            Console.TreatControlCAsInput = _restoreTreatControlCAsInput;
            Console.CursorVisible = true;
        }
        catch (IOException)
        {
        }
    }

    private static PtyRawMode? TryEnableWindows()
    {
        // 同 TerminalRawMode: 输入/输出其中一路不是本进程的终端时, 这个控制台不是我们拥有的, 不能接管。
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return null;

        try
        {
            var restoreTreatControlCAsInput = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            Console.CursorVisible = false;
            var input = GetStdHandle(StdInputHandle);
            if (input == 0 || !GetConsoleMode(input, out var originalMode))
                return new PtyRawMode(true, restoreTreatControlCAsInput: restoreTreatControlCAsInput);

            // 关掉行/回显/本地处理与鼠标输入记录: 否则宿主的鼠标上报会被控制台当成输入记录回显成 "^[[M…" 文本,
            // 混进 attach 画面。同时打开 VT 输入, 让终端的转义序列原样送达会话(鼠标报文由客户端过滤器整条丢弃)。
            var mode = originalMode
                & ~(EnableEchoInput | EnableLineInput | EnableProcessedInput | EnableMouseInput);
            SetConsoleMode(input, mode | EnableVirtualTerminalInput);
            return new PtyRawMode(
                true,
                restoreTreatControlCAsInput: restoreTreatControlCAsInput,
                consoleInputHandle: input,
                consoleInputMode: originalMode);
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

    private static PtyRawMode? TryEnableUnix()
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
            return new PtyRawMode(true, original);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
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
}
