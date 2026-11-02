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
    // Temporary CI111 investigation: observeSize records source queries without changing report or polling semantics.
    internal static async Task WatchSizeAsync(Func<int, int, CancellationToken, Task> report, CancellationToken cancellationToken, bool reportInitial = true,
        Action<(int Columns, int Rows)?>? observeSize = null)
    {
        var initial = ReadSize();
        observeSize?.Invoke(initial);
        if (initial is not var (columns, rows))
            return;
        if (reportInitial)
            await report(columns, rows, cancellationToken);
        while (true)
        {
            await Task.Delay(SizePollMilliseconds, cancellationToken);
            var current = ReadSize();
            observeSize?.Invoke(current);
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
        try
        {
            var (columns, rows) = (Console.WindowWidth, Console.WindowHeight);
            return rows >= MinimumRows && columns >= MinimumColumns ? (columns, rows) : null;
        }
        catch (Exception error) when (error is IOException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

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
