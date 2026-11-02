using System.Runtime.InteropServices;
using System.Text;

namespace Dsh.Pty;

/**
 * Hands this process's terminal to a byte tunnel (a PTY living elsewhere, e.g. on a remote host): raw input in, session output out, terminal size changes reported.
 * The detach key (Ctrl+]) leaves the tunnel without stopping the session behind it.
 */
public static class ConsoleTunnel
{
    /** Ctrl+] as typed in a raw terminal, the telnet/ssh-style escape that cannot collide with the shell's own keys. */
    public const byte DetachKey = 0x1d;

    public const string DetachKeyName = "Ctrl+]";

    /** How often the terminal size is compared: resizes do not interrupt blocking reads, so they are polled. */
    private const int SizePollMilliseconds = 200;

    /** Smallest size worth reporting; anything below comes from a terminal that is not really there. */
    private const int MinimumRows = 4;
    private const int MinimumColumns = 20;

    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    /**
     * Runs tunnel(input, output, token) on the raw terminal until the tunnel ends or the user presses the detach key.
     * resize(columns, rows, token) completes once with the current size before the tunnel starts, then runs whenever it changes.
     */
    public static async Task RunAsync(
        Func<Stream, Stream, CancellationToken, Task> tunnel,
        Func<int, int, CancellationToken, Task> resize,
        CancellationToken cancellationToken)
    {
        ConsoleCodePage.EnsureUtf8();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (ReadSize() is var (columns, rows))
            await resize(columns, rows, stop.Token);
        var output = Console.OpenStandardOutput();
        var input = new DetachKeyStream(ConsoleInput.OpenForRead(), stop);
        var sizeWatch = WatchSizeAsync(resize, stop.Token, reportInitial: false);
        try
        {
            using var raw = PtyRawMode.TryEnable();
            await tunnel(input, output, stop.Token);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await stop.CancelAsync();
            await IgnoreCancellationAsync(sizeWatch);
            // The session may have left the terminal on its alternate screen, without a cursor, or reporting the mouse; give the shell its terminal back.
            await output.WriteAsync(Encoding.UTF8.GetBytes($"\u001b[?1049l\u001b[?25h{PtyConsoleBridge.MouseDisableSequence}"), CancellationToken.None);
            await output.FlushAsync(CancellationToken.None);
        }
    }

    /** Reports the current terminal size, then every change; report failures are left to the callback, which may retry on the next change. */
    internal static async Task WatchSizeAsync(Func<int, int, CancellationToken, Task> report, CancellationToken cancellationToken, bool reportInitial = true)
    {
        var initial = ReadSize();
        if (initial is not var (columns, rows))
            return;
        if (reportInitial)
            await report(columns, rows, cancellationToken);
        while (true)
        {
            await Task.Delay(SizePollMilliseconds, cancellationToken);
            var current = ReadSize();
            if (current is not var (currentColumns, currentRows) || (currentColumns == columns && currentRows == rows))
                continue;
            (columns, rows) = (currentColumns, currentRows);
            await report(columns, rows, cancellationToken);
        }
    }

    internal static (int Columns, int Rows)? ReadSize()
    {
        if (!OperatingSystem.IsWindows())
        {
            var size = UnixPtyNative.ReadWindowSize(1) ?? UnixPtyNative.ReadWindowSize(0);
            return size is { Columns: >= MinimumColumns, Rows: >= MinimumRows } ? size : null;
        }
        if (Console.IsOutputRedirected)
            return null;
        // STDOUT may still name the main screen after the tunnel enters an alternate screen.
        // Open the active screen for each sample; retaining a handle would bind the watcher to the screen that was active at attach time.
        var output = CreateFileW("CONOUT$", GenericRead, ShareReadWrite, 0, OpenExisting, 0, 0);
        if (output == 0 || output == -1)
            return null;
        try
        {
            if (!GetConsoleScreenBufferInfo(output, out var info))
                return null;
            var columns = info.Window.Right - info.Window.Left + 1;
            var rows = info.Window.Bottom - info.Window.Top + 1;
            return rows >= MinimumRows && columns >= MinimumColumns ? (columns, rows) : null;
        }
        finally
        {
            CloseHandle(output);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenBufferInfo
    {
        public WindowsConsoleRecord.Coord Size;
        public WindowsConsoleRecord.Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public WindowsConsoleRecord.Coord MaximumWindowSize;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(string fileName, uint access, uint shareMode, nint securityAttributes, uint creationDisposition, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleScreenBufferInfo(nint output, out ScreenBufferInfo info);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /** Passes input through up to the detach key, then ends the stream and cancels the tunnel. */
    private sealed class DetachKeyStream(Stream inner, CancellationTokenSource detach) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (detach.IsCancellationRequested)
                return 0;
            var read = await inner.ReadAsync(buffer, cancellationToken);
            var key = buffer.Span[..read].IndexOf(DetachKey);
            if (key < 0)
                return read;
            await detach.CancelAsync();
            return key;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
