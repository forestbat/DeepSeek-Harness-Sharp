using Dsh.Pty;

namespace Dsh.Tests;

public class PtyDaemonTests
{
    private static readonly string TestRoot = Path.Combine(FindRepositoryRoot(), ".daemon-tests");

    [Fact]
    public async Task List_ReturnsEmpty_WhenNoSessionsExist()
    {
        if (OperatingSystem.IsWindows())
            return;

        var socketPath = CreateSocketPath();
        try
        {
            await using var daemon = new PtyDaemon(socketPath: socketPath);
            await daemon.StartAsync(TestContext.Current.CancellationToken);

            var sessions = await PtyDaemonClient.ListAsync(socketPath, null, TestContext.Current.CancellationToken);

            Assert.Empty(sessions);
        }
        finally
        {
            DeleteSocket(socketPath);
        }
    }

    [Fact]
    public async Task Start_ThenList_ContainsStartedSession()
    {
        if (OperatingSystem.IsWindows())
            return;

        var socketPath = CreateSocketPath();
        try
        {
            await using var daemon = new PtyDaemon(socketPath: socketPath);
            await daemon.StartAsync(TestContext.Current.CancellationToken);

            var started = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
            {
                FileName = "/bin/sh",
                Arguments = ["-c", "sleep 5"],
            }, socketPath, null, TestContext.Current.CancellationToken);

            var info = Assert.Single(await PtyDaemonClient.ListAsync(socketPath, null, TestContext.Current.CancellationToken));
            Assert.Equal(started.Id, info.Id);
            Assert.Equal("Running", info.Status);
        }
        finally
        {
            DeleteSocket(socketPath);
        }
    }

    [Fact]
    public async Task Host_ReadWrite_RoundTrips()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var host = new PtyHost();
        using var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "read line; echo GOT:$line"],
        }, cancellationToken: TestContext.Current.CancellationToken);
        await session.WriteAsync("hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var text = await ReadUntilAsync(session, "GOT:hello", TimeSpan.FromSeconds(4));
        Assert.Contains("GOT:hello", text);
    }

    [Fact]
    public async Task Host_ConcurrentReadThenDelayedWrite_RoundTrips()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var host = new PtyHost();
        using var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "read line; echo GOT:$line"],
        }, cancellationToken: TestContext.Current.CancellationToken);
        var readTask = ReadUntilAsync(session, "GOT:hello", TimeSpan.FromSeconds(4));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        await session.WriteAsync("hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var text = await readTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("GOT:hello", text);
    }

    private static async Task<string> ReadUntilAsync(PtySession session, string marker, TimeSpan timeout)
    {
        var text = new System.Text.StringBuilder();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var buffer = new byte[1024];
            var read = await session.ReadAsync(buffer);
            if (read == 0)
                break;
            text.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        return text.ToString();
    }

    [Fact]
    public async Task Attach_RelaysSessionInputAndOutput()
    {
        if (OperatingSystem.IsWindows())
            return;

        var socketPath = CreateSocketPath();
        try
        {
            await using var daemon = new PtyDaemon(socketPath: socketPath);
            await daemon.StartAsync(TestContext.Current.CancellationToken);
            var started = await PtyDaemonClient.StartAsync(new PtyDaemonStartParams
            {
                FileName = "/bin/sh",
                Arguments = ["-c", "read line; echo GOT:$line"],
            }, socketPath, null, TestContext.Current.CancellationToken);

            using var input = new OpenEndedInputStream("hello\n"u8.ToArray());
            using var output = new MemoryStream();
            await PtyDaemonClient.AttachAsync(started.Id, input, output, socketPath, null, TestContext.Current.CancellationToken);

            Assert.Contains("GOT:hello", System.Text.Encoding.UTF8.GetString(output.ToArray()));
        }
        finally
        {
            DeleteSocket(socketPath);
        }
    }

    private sealed class OpenEndedInputStream(byte[] payload) : Stream
    {
        private readonly MemoryStream _payload = new(payload);
        private readonly ManualResetEventSlim _closed = new(false);

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_payload.Position < _payload.Length)
                return _payload.Read(buffer, offset, count);
            _closed.Wait();
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_payload.Position < _payload.Length)
                return await _payload.ReadAsync(buffer, cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closed.Set();
                _payload.Dispose();
                _closed.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task Dispose_RemovesSocketFile()
    {
        if (OperatingSystem.IsWindows())
            return;

        var socketPath = CreateSocketPath();
        var daemon = new PtyDaemon(socketPath: socketPath);
        await daemon.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(socketPath));

        await daemon.DisposeAsync();

        Assert.False(File.Exists(socketPath));
    }

    private static string CreateSocketPath()
    {
        Directory.CreateDirectory(TestRoot);
        var name = $"d{Guid.NewGuid():N}"[..8] + ".sock";
        return Path.Combine(TestRoot, name);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static void DeleteSocket(string socketPath)
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);
    }
}