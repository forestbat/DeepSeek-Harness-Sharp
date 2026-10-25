namespace Dsh.Tests;

/** tui kill-pty 指定 id 的后端: 只终止被点名的那个 daemon PTY 会话。 */
public sealed class PtyDaemonKillTests
{
    [Fact(Timeout = 5000)]
    public async Task Kill_Stops_Only_The_Requested_Session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("pk");
        try
        {
            var socketPath = Path.Combine(root, "dsh.sock");
            var portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);

            var first = await PtyDaemonRetry.Async(() => StartSessionAsync(socketPath, portFile, cancellationToken), cancellationToken);
            var second = await PtyDaemonRetry.Async(() => StartSessionAsync(socketPath, portFile, cancellationToken), cancellationToken);

            var killed = await PtyDaemonRetry.Async(
                () => PtyDaemonClient.KillAsync(first.Id, socketPath, portFile, cancellationToken), cancellationToken);
            Assert.Equal([first.Id], killed);

            // 终止后会话从列表消失可能需要一瞬(宿主异步回收), 轮询到稳定为止。
            var gone = false;
            for (var attempt = 0; attempt < 200 && !gone; attempt++)
            {
                var listing = await PtyDaemonRetry.Async(
                    () => PtyDaemonClient.ListAsync(socketPath, portFile, cancellationToken), cancellationToken);
                gone = listing.All(session => session.Id != first.Id);
                if (!gone)
                    await Task.Delay(25, cancellationToken);
            }
            Assert.True(gone, "被终止的会话仍在列表中");
            var remaining = await PtyDaemonRetry.Async(
                () => PtyDaemonClient.ListAsync(socketPath, portFile, cancellationToken), cancellationToken);
            Assert.Contains(remaining, session => session.Id == second.Id);

            await PtyDaemonRetry.Async(() => PtyDaemonClient.KillAsync(second.Id, socketPath, portFile, cancellationToken), cancellationToken);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact(Timeout = 5000)]
    public async Task Kill_Unknown_Id_Reports_Not_Found()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("pku");
        try
        {
            var socketPath = Path.Combine(root, "dsh.sock");
            var portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);

            // daemon 的管道/端口握手在负载下可能尚未就绪: 对连接级 IOException 重试到拿到响应为止。
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => PtyDaemonRetry.Async(() => PtyDaemonClient.KillAsync("pty-does-not-exist", socketPath, portFile, cancellationToken), cancellationToken));
            Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** 起一个长时间驻留的会话(不是交互 shell: 无客户端时交互 shell 会因 stdin EOF 自行退出, 负载下不可靠)。 */
    private static Task<PtyDaemonSessionDto> StartSessionAsync(
        string socketPath,
        string? portFile,
        CancellationToken cancellationToken)
    {
        var (fileName, arguments) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (string[])["/c", "ping -n 60 127.0.0.1 > nul"])
            : ("/bin/sh", (string[])["-c", "sleep 60"]);
        return PtyDaemonClient.StartAsync(
            new PtyDaemonStartParams
            {
                FileName = fileName,
                Arguments = [.. arguments],
                Rows = 24,
                Columns = 80,
            },
            socketPath,
            portFile,
            cancellationToken);
    }
}
