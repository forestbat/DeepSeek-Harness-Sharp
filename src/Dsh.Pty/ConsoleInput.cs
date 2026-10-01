using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Dsh.Pty;

/**
 * 终端原始输入: Unix 走 fd 0 的 poll+read(2), Windows 非控制台(管道/PTY)走原始字节; 真控制台走记录(见 ConsoleInputSession)。
 * 不能用 Console.OpenStandardInput(): Linux 上 .NET 的控制台机制会接管 fd 0, 直读流第一次读到一截之后就永远阻塞不返回;
 * Windows 上它给的是"按键字符"流, 鼠标上报会被当文本交出来。
 */
internal static class ConsoleInput
{
    /** 供隧道输入泵读取的流: Unix 走 fd 0 的 poll+read, Windows 走控制台/管道原始字节, 都支持取消。 */
    public static Stream OpenForRead()
        => OperatingSystem.IsWindows() ? new WindowsRawConsoleStream() : new UnixStdinStream();

    private const short PollIn = 0x0001;
    private const int PollTimeoutMs = 200;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern int readBytes(int fd, [Out] byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll([In, Out] PollFd[] fds, int nfds, int timeout);

    /** Unix: fd 0 上的 poll+read, 支持取消。 */
    internal sealed class UnixStdinStream : Stream
    {
        private readonly byte[] _scratch = new byte[8192];

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fds = new[] { new PollFd { Fd = 0, Events = PollIn } };
                var ready = poll(fds, fds.Length, PollTimeoutMs);
                if (ready < 0)
                    return 0;
                if (ready == 0 || fds[0].Revents == 0)
                {
                    // 让出执行权: 同步 poll 循环会把调用方(隧道)同一线程上的另一个泵饿死。
                    await Task.Yield();
                    continue;
                }

                var request = Math.Min(buffer.Length, _scratch.Length);
                var read = readBytes(0, _scratch, request);
                if (read < 0)
                {
                    await Task.Yield();
                    continue;
                }

                if (read == 0)
                    return 0;
                _scratch.AsSpan(0, read).CopyTo(buffer.Span);
                return read;
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }
    }

    /**
     * Windows 非控制台(stdin 是管道: ConPTY/PTY 夹具): 按原始字节读。
     * 生产者线程阻塞读, 通过 Channel 异步交给上层 —— 不能用 BlockingCollection.TryTake(Timeout.Infinite), 那是同步阻塞,
     * 会把隧道里另一个泵饿死(曾导致 TUI 启动卡死)。
     */
    internal sealed class WindowsRawConsoleStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private int _pumpStarted;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set { }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            StartPump();
            try
            {
                while (await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!_chunks.Reader.TryRead(out var chunk))
                        continue;
                    var take = Math.Min(chunk.Length, buffer.Length);
                    chunk.AsSpan(0, take).CopyTo(buffer.Span);
                    if (take < chunk.Length)
                        _chunks.Writer.TryWrite(chunk[take..]);
                    return take;
                }
            }
            catch (OperationCanceledException)
            {
            }

            return 0;
        }

        private void StartPump()
        {
            if (Interlocked.Exchange(ref _pumpStarted, 1) != 0)
                return;
            Task.Factory.StartNew(
                () =>
                {
                    var handle = WindowsConsoleRecord.InputHandle();
                    var buffer = new byte[4096];
                    try
                    {
                        while (true)
                        {
                            if (!ReadFile(handle, buffer, buffer.Length, out var read, IntPtr.Zero) || read <= 0)
                                break;
                            if (!_chunks.Writer.TryWrite(buffer[..read]))
                                break;
                        }
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        _chunks.Writer.TryComplete();
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => 0;

        public override void SetLength(long value)
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        [DllImport("kernel32", SetLastError = true)]
        private static extern bool ReadFile(IntPtr handle, [Out] byte[] buffer, int numberOfBytesToRead, out int numberOfBytesRead, IntPtr overlapped);
    }
}
