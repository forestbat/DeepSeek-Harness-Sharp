using Dsh.Pty;

namespace Dsh.Tests;

/** 跨进程归属与输入通道的协议往返: identify / publish-panes / control-send / control-read。 */
public sealed class PtyOwnershipTests
{
    [Fact(Timeout = 5000)]
    public async Task Identify_Publish_And_Control_RoundTrip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = TempTree.CreateSocketDirectory("pown");
        try
        {
            var socketPath = Path.Combine(root, "dsh.sock");
            TempTree.AssertFitsMacOsSocketLimit(socketPath);
            var portFile = OperatingSystem.IsWindows() ? Path.Combine(root, "dsh.port") : null;
            await using var daemon = new PtyDaemon(new PtyHost(), socketPath, portFile);
            await daemon.StartAsync(cancellationToken);

            var (fileName, arguments) = OperatingSystem.IsWindows()
                ? ("cmd.exe", (string[])["/c", "ping -n 60 127.0.0.1 > nul"])
                : ("/bin/sh", (string[])["-c", "sleep 10"]);
            var started = await PtyDaemonRetry.StartAsync(
                new PtyDaemonStartParams { FileName = fileName, Arguments = [.. arguments], Rows = 10, Columns = 40 },
                socketPath,
                portFile,
                cancellationToken);

            await PtyDaemonClient.IdentifyAsync(started.Id, "session-abc", socketPath, portFile, cancellationToken);
            await PtyDaemonClient.PublishPanesAsync(
                started.Id,
                "session-abc",
                [new PtyPaneSnapshotDto { Id = 0, Kind = "chat", Title = "chat", SessionId = "session-abc", Focused = true, Lines = ["hello"] }],
                socketPath,
                portFile,
                cancellationToken);

            var listed = await PtyDaemonClient.ListAsync(socketPath, portFile, cancellationToken);
            var dto = Assert.Single(listed, session => session.Id == started.Id);
            Assert.Equal("session-abc", dto.AgentSessionId);
            Assert.NotNull(dto.Panes);
            Assert.Equal("hello", Assert.Single(dto.Panes!).Lines![0]);

            var seq = await PtyDaemonClient.ControlSendAsync(started.Id, "text", 0, "ping", socketPath, portFile, cancellationToken);
            Assert.True(seq > 0);

            var messages = await PtyDaemonClient.ControlReadAsync(started.Id, 0, socketPath, portFile, cancellationToken);
            var message = Assert.Single(messages);
            Assert.Equal("text", message.Kind);
            Assert.Equal(0, message.PaneId);
            Assert.Equal("ping", message.Payload);
        }
        finally
        {
            TempTree.Delete(root);
        }
    }
}
