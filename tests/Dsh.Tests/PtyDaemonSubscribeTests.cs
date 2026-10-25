using System.Text;
using Dsh.Pty;

namespace Dsh.Tests;

/** 窗格级订阅(§19 阶段 2): daemon pty 可被多方同时订阅, 不独占会话, 输入写回该 pty 的前台进程。 */
[Collection(SerialProcessCollection.Name)]
public sealed class PtyDaemonSubscribeTests
{
    /** 真实 ConPTY 子进程在负载下启动可能偏慢: 给足标记到达时间(事实级超时上限 30s)。 */
    private static readonly TimeSpan MarkerTimeout = TimeSpan.FromSeconds(5);
    [Fact(Explicit = true)]
    public async Task Subscribe_StreamsSnapshotAndOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("sub");
        try
        {
            var (socketPath, portFile) = Paths(root);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);
            var started = await PtyDaemonRetry.StartAsync(StartParams(OutputShell("SNAPMARK")), socketPath, portFile, cancellationToken);

            await using var session = await PtyDaemonRetry.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);
            Assert.Equal(started.Id, session.Id);

            var received = await ReadUntilAsync(session, "SNAPMARK", MarkerTimeout);
            if (!received.Contains("SNAPMARK", StringComparison.Ordinal))
            {
                // 区分"子进程没跑/输出没广播"与"流被提前关掉": 前者 daemon 侧屏上也没有标记。
                var snapshot = await PtyDaemonRetry.SnapshotAsync(started.Id, socketPath, portFile, cancellationToken);
                Assert.Fail($"SNAPMARK 未到达: streamClosed={session.Exited} onDaemonScreen={snapshot.Contains("SNAPMARK", StringComparison.Ordinal)} received={received.Length}B");
            }
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** 非独占: 两个订阅者都要看到同一段输出(独占 attach 的单一读语义不适用于窗格级视图)。 */
    [Fact(Explicit = true)]
    public async Task Subscribe_IsNonExclusive_TwoSubscribersSeeSameOutput()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("sub2");
        try
        {
            var (socketPath, portFile) = Paths(root);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);
            var started = await PtyDaemonRetry.StartAsync(StartParams(InteractiveShell()), socketPath, portFile, cancellationToken);

            await using var first = await PtyDaemonRetry.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);
            await using var second = await PtyDaemonRetry.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);

            var (input, expected) = MarkerCommand();
            first.Send(Encoding.UTF8.GetBytes(input));

            Assert.Contains(expected, await ReadUntilAsync(first, expected, MarkerTimeout));
            Assert.Contains(expected, await ReadUntilAsync(second, expected, MarkerTimeout));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** 订阅把输入写进 pty 的前台进程: 只出现在"进程真的执行了"时的输出(不是输入回显)。 */
    [Fact(Explicit = true)]
    public async Task Subscribe_RelaysInputToForegroundProcess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("subin");
        try
        {
            var (socketPath, portFile) = Paths(root);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);
            var started = await PtyDaemonRetry.StartAsync(StartParams(InteractiveShell()), socketPath, portFile, cancellationToken);

            await using var session = await PtyDaemonRetry.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);
            var (input, expected) = MarkerCommand();
            session.Send(Encoding.UTF8.GetBytes(input));

            var received = await ReadUntilAsync(session, expected, MarkerTimeout);
            if (!received.Contains(expected, StringComparison.Ordinal))
            {
                // 区分"按键没写进去/子进程没跑"与"流被提前关掉": 前者 daemon 侧屏上也没有标记。
                var snapshot = await PtyDaemonRetry.SnapshotAsync(started.Id, socketPath, portFile, cancellationToken);
                Assert.Fail($"{expected} 未到达: streamClosed={session.Exited} onDaemonScreen={snapshot.Contains(expected, StringComparison.Ordinal)} received={received.Length}B");
            }
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** 订阅不改变会话的 attached 状态: 它不是独占 attach, 会话照常可被别的 proxy 接管。 */
    [Fact(Explicit = true)]
    public async Task Subscribe_DoesNotTakeOverTheSession()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("sub3");
        try
        {
            var (socketPath, portFile) = Paths(root);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);
            var started = await PtyDaemonRetry.StartAsync(
                StartParams(Shell("echo idle & ping -n 60 127.0.0.1 > nul", "echo idle; sleep 60")),
                socketPath,
                portFile,
                cancellationToken);

            await using var session = await PtyDaemonRetry.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);

            var info = Assert.Single(await PtyDaemonRetry.ListAsync(socketPath, portFile, cancellationToken));
            Assert.False(info.IsAttached);
            Assert.Equal(nameof(PtySessionStatus.Running), info.Status);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task Snapshot_ReturnsCurrentScreenText()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("sub4");
        try
        {
            var (socketPath, portFile) = Paths(root);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);
            var started = await PtyDaemonRetry.StartAsync(
                StartParams(OutputShell("SNAPMARK")), socketPath, portFile, cancellationToken);

            // 先经订阅确认画面内容已进 daemon 的 VT 屏, 再取一次快照(轮询会反复新建连接, 负载下易被连接超时打断)。
            await using var session = await PtyDaemonRetry.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);
            await ReadUntilAsync(session, "SNAPMARK", MarkerTimeout);

            var snapshot = await PtyDaemonRetry.SnapshotAsync(started.Id, socketPath, portFile, cancellationToken);
            Assert.Contains("SNAPMARK", snapshot);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task Subscribe_UnknownSession_Throws()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("sub5");
        try
        {
            var (socketPath, portFile) = Paths(root);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => PtyDaemonRetry.SubscribeAsync("pty-missing", socketPath, portFile, cancellationToken));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** 会话退出后订阅通道结束: 读返回 0 而不是永久阻塞。 */
    [Fact(Explicit = true)]
    public async Task Subscribe_CompletesWhenSessionExits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("sub6");
        try
        {
            var (socketPath, portFile) = Paths(root);
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);
            var started = await PtyDaemonRetry.StartAsync(StartParams(InteractiveShell()), socketPath, portFile, cancellationToken);

            // The shell exits on the subscriber's own command, so the exit always happens after the subscription exists.
            await using var session = await PtyDaemonRetry.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);
            session.Send(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? "exit\r" : "exit\n"));
            await session.Completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

            var buffer = new byte[256];
            var read = await session.ReadAsync(buffer, cancellationToken);
            while (read > 0)
                read = await session.ReadAsync(buffer, cancellationToken);
            Assert.Equal(0, read);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Explicit = true)]
    public async Task Subscribe_AfterProcessAndOutputExit_ReplaysFinalScreenAndCompletes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("sub7");
        try
        {
            var (socketPath, portFile) = Paths(root);
            using var host = new PtyHost();
            await using var daemon = new PtyDaemon(host, socketPath, portFile);
            await daemon.StartAsync(cancellationToken);
            var started = await PtyDaemonRetry.StartAsync(StartParams(InteractiveShell()), socketPath, portFile, cancellationToken);
            await using var first = await PtyDaemonClient.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);
            var (input, expected) = MarkerCommand();
            first.Send(Encoding.UTF8.GetBytes(input));
            Assert.Contains(expected, await ReadUntilAsync(first, expected, MarkerTimeout));
            first.Send(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? "exit 7\r" : "exit 7\n"));
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var process = Assert.IsType<PtySession>(host.Get(started.Id));
            await process.WaitForExitAsync(cancellationToken);
            Assert.Equal(PtySessionStatus.Exited, process.Status);
            Assert.Equal(7, process.ExitCode);

            // The late client arrives only after both real process exit and the first output EOF.
            await using var late = await PtyDaemonClient.SubscribeAsync(started.Id, socketPath, portFile, cancellationToken);
            await late.Completion.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            var snapshot = new StringBuilder();
            var buffer = new byte[1024];
            int read;
            while ((read = await late.ReadAsync(buffer, cancellationToken)) > 0)
                snapshot.Append(Encoding.UTF8.GetString(buffer, 0, read));
            Assert.Contains(expected, snapshot.ToString());
            Assert.True(late.Exited);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static (string SocketPath, string? PortFile) Paths(string root)
        => (Path.Combine(root, "dsh.sock"), OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null);

    private static PtyDaemonStartParams StartParams((string FileName, string[] Arguments) command)
        => new()
        {
            FileName = command.FileName,
            Arguments = [.. command.Arguments],
            Rows = 24,
            Columns = 80,
        };

    /** 先输出标记再驻留的脚本: 订阅要能看到"订阅之后仍在产生"的输出。 */
    private static (string FileName, string[] Arguments) OutputShell(string marker)
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", $"echo {marker} & ping -n 30 127.0.0.1 > nul"])
            : ("/bin/sh", ["-c", $"echo {marker}; sleep 30"]);

    /** 同一脚本的 Windows/Unix 两版。 */
    private static (string FileName, string[] Arguments) Shell(string windows, string unix)
        => OperatingSystem.IsWindows()
            ? ("cmd.exe", ["/c", windows])
            : ("/bin/sh", ["-c", unix]);

    private static (string FileName, string[] Arguments) InteractiveShell()
        => OperatingSystem.IsWindows()
            // /v:on: 延迟展开, 否则整行在解析时就展开了 %var%。
            ? ("cmd.exe", ["/v:on"])
            : ("/bin/sh", []);

    /** 一条"只有前台进程真的执行才会出现该输出"的命令(输入的约定标记不是最终输出)。 */
    private static (string Input, string Expected) MarkerCommand()
        => OperatingSystem.IsWindows()
            ? ("set /a m=6*7 & echo LIVE-!m!\r", "LIVE-42")
            : ("echo LIVE-$((6*7))\n", "LIVE-42");

    private static async Task<string> ReadUntilAsync(PtyRemoteSession session, string marker, TimeSpan timeout)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeoutSource.CancelAfter(timeout);
        var text = new StringBuilder();
        var buffer = new byte[1024];
        try
        {
            while (!text.ToString().Contains(marker, StringComparison.Ordinal))
            {
                var read = await session.ReadAsync(buffer, timeoutSource.Token);
                if (read == 0)
                    break;
                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
        }
        catch (OperationCanceledException)
        {
        }

        return text.ToString();
    }
}


