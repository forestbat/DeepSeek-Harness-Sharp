using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Dsh.Pty;

public static class PtyConsoleBridge
{
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

    private readonly bool _active;
    private readonly byte[]? _original;
    private readonly bool _restoreTreatControlCAsInput;
    private bool _disposed;

    private PtyRawMode(bool active, byte[]? original = null, bool restoreTreatControlCAsInput = false)
    {
        _active = active;
        _original = original;
        _restoreTreatControlCAsInput = restoreTreatControlCAsInput;
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
            Console.TreatControlCAsInput = _restoreTreatControlCAsInput;
            Console.CursorVisible = true;
        }
        catch (IOException)
        {
        }
    }

    private static PtyRawMode? TryEnableWindows()
    {
        if (Console.IsInputRedirected)
            return null;

        try
        {
            var restoreTreatControlCAsInput = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            Console.CursorVisible = false;
            return new PtyRawMode(true, restoreTreatControlCAsInput: restoreTreatControlCAsInput);
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
}
