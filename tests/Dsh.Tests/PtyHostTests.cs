using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public class PtyHostTests
{
    private const int FailureExitCode = 7;

    [Theory(Timeout = 5000)]
    [InlineData(0)]
    [InlineData(FailureExitCode)]
    public async Task ProcessCompletion_PublishesTheActualExitStatus(int exitCode)
    {
        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? ["/d", "/c", $"exit /b {exitCode}"] : ["-c", $"exit {exitCode}"],
        }, cancellationToken: TestContext.Current.CancellationToken);
        await session.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(PtySessionStatus.Exited, session.Status);
        Assert.Equal(exitCode, session.ExitCode);
    }

    [Fact(Timeout = 5000)]
    public async Task Host_Starts_Lists_Reads_And_Stops()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "echo pty-ready; sleep 5"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        var info = Assert.Single(host.List());
        Assert.Equal(session.Id, info.Id);
        Assert.Equal(PtySessionStatus.Running, info.Status);
        Assert.Equal("echo pty-ready; sleep 5", info.Command.Split("-c ")[^1]);

        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Contains("pty-ready", Encoding.UTF8.GetString(buffer, 0, read));

        Assert.True(await host.StopAsync(session.Id.ToString()));
        Assert.DoesNotContain(host.List(), entry => entry.Id == session.Id);
    }

    [Fact(Timeout = 5000)]
    public async Task Resize_UpdatesPtyWindowSize()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "read line; stty size"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        session.Resize(30, 120);
        Assert.Equal(PtySessionStatus.Running, session.Status);
        await session.WriteAsync("\n"u8.ToArray(), TestContext.Current.CancellationToken);

        // 以子进程自己报告的窗口尺寸为准: Darwin arm64 上 ioctl 曾读到垃圾 winsize 而不报错。
        var buffer = new byte[4096];
        var output = new StringBuilder();
        while (!output.ToString().Contains("30 120"))
        {
            var read = await session.ReadAsync(buffer, TestContext.Current.CancellationToken);
            if (read == 0)
                break;
            output.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        Assert.Contains("30 120", output.ToString());

        await host.StopAsync(session.Id.ToString());
    }

    [Fact(Timeout = 5000)]
    public async Task Write_And_Read_RoundTrips()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "/bin/sh",
            Arguments = ["-c", "cat"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        await session.WriteAsync("hello pty\n"u8.ToArray(), TestContext.Current.CancellationToken);
        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer, TestContext.Current.CancellationToken);
        Assert.Contains("hello pty", Encoding.UTF8.GetString(buffer, 0, read));

        await host.StopAsync(session.Id.ToString());
    }

    [Fact(Timeout = 5000)]
    public async Task Windows_ConPty_Starts_And_Reads()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var direct = ConPtySession.Start(new PtyStartInfo
        {
            FileName = "cmd.exe",
            Arguments = ["/c", "echo direct-conpty-ready"],
        });
        using var directCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await ReadUntil(direct.Stream, "direct-conpty-ready", directCancellation.Token);
        Assert.Contains("direct-conpty-ready", received);
        await direct.StopAsync();

        using var host = new PtyHost();
        var session = await host.StartAsync(new PtyStartInfo
        {
            FileName = "cmd.exe",
            // 断言状态必须是 Running: 用常驻命令(echo 后 ping 等待), 否则 /c echo 会立刻退出。
            Arguments = ["/c", "echo conpty-ready & ping -n 30 127.0.0.1 > nul"],
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(PtySessionStatus.Running, session.Status);
        var backend = typeof(PtySession).GetField("_conPty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(session);
        Assert.NotNull(backend);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        var read = await session.ReadAsync(buffer, cancellation.Token);
        Assert.True(read > 0);

        session.Resize(30, 120);
        Assert.True(await host.StopAsync(session.Id.ToString()));
    }

    [Fact(Timeout = 5000)]
    public async Task Windows_ConPty_Parallel_Starts_Keep_Input_And_Output_Isolated()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        using var startBarrier = new Barrier(2);
        await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(async () =>
        {
            startBarrier.SignalAndWait(cancellation.Token);
            using var session = ConPtySession.Start(new PtyStartInfo
            {
                FileName = "cmd.exe",
                Arguments = ["/d", "/q", "/v:on"],
            });
            try
            {
                // The answer is computed by this shell, not present in its banner
                // or input echo, and must return through this session's own pipe.
                var expected = $"conpty-{index}-{42 + index}";
                var command = $"set /a result=6*7+{index} & echo conpty-{index}-!result!\r";
                await session.Input.WriteAsync(Encoding.UTF8.GetBytes(command), cancellation.Token);
                var received = await ReadUntil(session.Stream, expected, cancellation.Token);
                Assert.Contains(expected, received);
                Assert.DoesNotContain($"conpty-{1 - index}-{43 - index}", received);
                await session.Input.WriteAsync(Encoding.UTF8.GetBytes($"exit {index + 3}\r"), cancellation.Token);
                Assert.Equal(index + 3, await session.Completion.WaitAsync(cancellation.Token));
            }
            finally
            {
                await session.StopAsync();
            }
        }, cancellation.Token)));
    }

    private static async Task<string> ReadUntil(Stream stream, string expected, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var received = new List<byte>();
        while (!Encoding.UTF8.GetString(received.ToArray()).Contains(expected))
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            received.AddRange(buffer.AsSpan(0, read).ToArray());
        }
        return Encoding.UTF8.GetString(received.ToArray());
    }
}
