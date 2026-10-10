namespace Dsh.Tests;

/** tui kill-pty 指定 id 的后端: 只终止被点名的那个 daemon PTY 会话。 */
public sealed class PtyDaemonKillTests
{
    [Fact]
    public async Task Kill_Stops_Only_The_Requested_Session()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateDirectory("pty-kill");
        try
        {
            var socketPath = Path.Combine(root, "dsh.sock");
            var portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);

            var shell = PtyShell.Resolve();
            var first = await StartSessionAsync(shell, socketPath, portFile, cancellationToken);
            var second = await StartSessionAsync(shell, socketPath, portFile, cancellationToken);

            var killed = await PtyDaemonClient.KillAsync(first.Id, socketPath, portFile, cancellationToken);
            Assert.Equal([first.Id], killed);

            var remaining = await PtyDaemonClient.ListAsync(socketPath, portFile, cancellationToken);
            Assert.DoesNotContain(remaining, session => session.Id == first.Id);
            Assert.Contains(remaining, session => session.Id == second.Id);

            await PtyDaemonClient.KillAsync(second.Id, socketPath, portFile, cancellationToken);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    [Fact]
    public async Task Kill_Unknown_Id_Reports_Not_Found()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateDirectory("pty-kill-unknown");
        try
        {
            var socketPath = Path.Combine(root, "dsh.sock");
            var portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PtyDaemonClient.KillAsync("pty-does-not-exist", socketPath, portFile, cancellationToken));
            Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    private static Task<PtyDaemonSessionDto> StartSessionAsync(
        string shell,
        string socketPath,
        string? portFile,
        CancellationToken cancellationToken)
        => PtyDaemonClient.StartAsync(
            new PtyDaemonStartParams
            {
                FileName = shell,
                Arguments = [.. PtyShell.Arguments(shell)],
                Rows = 24,
                Columns = 80,
            },
            socketPath,
            portFile,
            cancellationToken);
}
