using System.Runtime.InteropServices;

namespace Dsh.Pty;

/**
 * 终端输入: Windows 走 .NET 控制台流(ConPTY 下正常), Unix 直接 poll+read(2) fd 0。
 * 不能用 Console.OpenStandardInput(): Linux 上 .NET 的控制台机制会接管 fd 0(否则 ReadKey 无法工作),
 * 直读流第一次读到一截之后就永远阻塞不返回 —— 表现为 attach 后第一批输入生效、之后的按键全部丢失。
 */
internal static class ConsoleInput
{
    public static void DrainQueued()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                while (Console.KeyAvailable)
                    Console.ReadKey(intercept: true);
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        try
        {
            var flags = fcntl(0, FGetFl, 0);
            if (flags < 0)
                return;
            fcntl(0, FSetFl, flags | ONonBlock);
            try
            {
                var buffer = new byte[128];
                while (readBytes(0, buffer, buffer.Length) > 0)
                {
                }
            }
            finally
            {
                fcntl(0, FSetFl, flags);
            }
        }
        catch (Exception)
        {
        }
    }

    /** 供隧道输入泵读取的流: Unix 下是 fd 0 上的 poll+read, 支持取消。 */
    public static Stream OpenForRead()
        => OperatingSystem.IsWindows() ? Console.OpenStandardInput() : new UnixStdinStream();

    private const short PollIn = 0x0001;
    private const int FGetFl = 3;
    private const int FSetFl = 4;
    private const int ONonBlock = 0x800;
    private const int PollTimeoutMs = 200;

    private sealed class UnixStdinStream : Stream
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
                {
                    Dsh.Pty.ZzInputTrace.Log("unix-stdin", $"poll<0 errno={Marshal.GetLastWin32Error()} -> EOF");
                    return 0;
                }
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

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);
}
