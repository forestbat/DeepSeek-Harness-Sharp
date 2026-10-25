using Dsh.Pty;

namespace Dsh.Tests;

public class PtyDaemonTests(ITestOutputHelper output)
{
    private static readonly string TestRoot = Path.Combine(FindRepositoryRoot(), ".daemon-tests");
    private static readonly TimeSpan ReadReleaseTimeout = TimeSpan.FromSeconds(5);
    private const int ExitCodeAfterOutputCloses = 7;
    private const int ExitDelayAfterOutputClosesSeconds = 1;

    [Fact(Timeout = 5000)]
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

    [Fact(Timeout = 5000)]
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
                Arguments = ["-c", "read line"],
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

    [Fact(Timeout = 5000)]
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

    [Fact(Timeout = 5000)]
    public async Task Host_ConcurrentReadThenDelayedWrite_RoundTrips()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var host = new PtyHost();
        using var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "printf READY; read line; echo GOT:$line"],
        }, cancellationToken: TestContext.Current.CancellationToken);
        var ready = await ReadUntilAsync(session, "READY", TimeSpan.FromSeconds(4));
        Assert.Contains("READY", ready);
        var readTask = ReadUntilAsync(session, "GOT:hello", TimeSpan.FromSeconds(4));
        // The real child has produced output; the next read must wait for our input, not a scheduling delay.
        Assert.False(readTask.IsCompleted);
        await session.WriteAsync("hello\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var text = await readTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Contains("GOT:hello", text);
    }

    [Fact(Timeout = 5000)]
    public async Task Host_Read_SmallBuffers_DrainsPendingChunk_WhileChildWaitsForInput()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var host = new PtyHost();
        using var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "printf READY; read line; printf DONE"],
        }, cancellationToken: TestContext.Current.CancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        Assert.Equal(0, await session.ReadAsync(Memory<byte>.Empty, deadline.Token));
        var buffer = new byte[1];
        var text = new System.Text.StringBuilder();
        while (text.Length < "READY".Length)
        {
            Assert.Equal(1, await session.ReadAsync(buffer, deadline.Token));
            text.Append((char)buffer[0]);
        }
        Assert.Equal("READY", text.ToString());
        await session.WriteAsync("\n"u8.ToArray(), deadline.Token);
        while (!text.ToString().Contains("DONE", StringComparison.Ordinal))
        {
            var count = await session.ReadAsync(buffer, deadline.Token);
            if (count == 0)
                break;
            text.Append((char)buffer[0]);
        }
        Assert.Contains("DONE", text.ToString());
    }

    private async Task<string> ReadUntilAsync(PtySession session, string marker, TimeSpan timeout)
    {
        var text = new System.Text.StringBuilder();
        var buffer = new byte[1024];
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var pendingReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(timeout);
        while (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var pendingRead = session.ReadAsync(buffer, pendingReadCancellation.Token).AsTask();
            int read;
            try
            {
                // WaitAsync accepts an already-completed read even if its continuation ran after the deadline.
                // This drains queued PTY bytes without extending the time allowed for new output.
                read = await pendingRead.WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                await pendingReadCancellation.CancelAsync();
                try
                {
                    await pendingRead;
                }
                catch (OperationCanceledException) when (pendingReadCancellation.IsCancellationRequested)
                {
                }
                break;
            }
            if (read == 0)
                break;
            text.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        if (!text.ToString().Contains(marker, StringComparison.Ordinal))
        {
            output.WriteLine($"PTY marker '{marker}' missing after {System.Diagnostics.Stopwatch.GetElapsedTime(started)}; Status={session.Status}; ExitCode={session.ExitCode}");
            output.WriteLine("PTY snapshot: " + System.Text.Json.JsonSerializer.Serialize(System.Text.Encoding.UTF8.GetString(session.Snapshot())));
        }
        return text.ToString();
    }

    [Fact(Timeout = 5000)]
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
                Arguments = ["-c", $"read line; echo GOT:$line; exec </dev/null >/dev/null 2>&1; sleep {ExitDelayAfterOutputClosesSeconds}; exit {ExitCodeAfterOutputCloses}"],
            }, socketPath, null, TestContext.Current.CancellationToken);

            using var input = new OpenEndedInputStream("hello\n"u8.ToArray());
            using var output = new MemoryStream();
            await PtyDaemonClient.AttachAsync(started.Id, input, output, socketPath, null, TestContext.Current.CancellationToken);

            Assert.Contains("GOT:hello", System.Text.Encoding.UTF8.GetString(output.ToArray()));
            var info = Assert.Single(await PtyDaemonClient.ListAsync(socketPath, null, TestContext.Current.CancellationToken));
            Assert.Equal("Exited", info.Status);
            Assert.Equal(ExitCodeAfterOutputCloses, info.ExitCode);
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
            if (!_closed.Wait(ReadReleaseTimeout))
                throw new TimeoutException($"OpenEndedInputStream 在 {ReadReleaseTimeout.TotalSeconds:0} s 内未被释放，同步读取将永久阻塞");
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

    [Fact(Timeout = 5000)]
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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeepSeek-Harness-Sharp.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static void DeleteSocket(string socketPath)
    {
        if (File.Exists(socketPath))
            File.Delete(socketPath);
    }
}
