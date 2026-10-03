using Dsh.Pty;

namespace Dsh.Tests;

/** 跨进程归属与输入通道的协议往返: identify / publish-panes / control-send / control-read。 */
public sealed class PtyOwnershipTests
{
    [Fact]
    public async Task Identify_Publish_And_Control_RoundTrip()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("daemon socket tests run on Unix");
            return;
        }
        if (!File.Exists("/bin/sh"))
        {
            Assert.Skip("/bin/sh unavailable");
            return;
        }

        var socketPath = Path.Combine(Path.GetTempPath(), $"dsh-pty-own-{Guid.NewGuid():N}", "dsh.sock");
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        try
        {
            await using var daemon = new PtyDaemon(socketPath: socketPath);
            await daemon.StartAsync(TestContext.Current.CancellationToken);

            var started = await PtyDaemonClient.StartAsync(
                new PtyDaemonStartParams { FileName = "/bin/sh", Arguments = ["-c", "sleep 10"], Rows = 10, Columns = 40 },
                socketPath,
                null,
                TestContext.Current.CancellationToken);

            await PtyDaemonClient.IdentifyAsync(started.Id, "session-abc", socketPath, null, TestContext.Current.CancellationToken);
            await PtyDaemonClient.PublishPanesAsync(
                started.Id,
                "session-abc",
                [new PtyPaneSnapshotDto { Id = 0, Kind = "chat", Title = "chat", SessionId = "session-abc", Focused = true, Lines = ["hello"] }],
                socketPath,
                null,
                TestContext.Current.CancellationToken);

            var listed = await PtyDaemonClient.ListAsync(socketPath, null, TestContext.Current.CancellationToken);
            var dto = Assert.Single(listed, session => session.Id == started.Id);
            Assert.Equal("session-abc", dto.AgentSessionId);
            Assert.NotNull(dto.Panes);
            Assert.Equal("hello", Assert.Single(dto.Panes!).Lines![0]);

            var seq = await PtyDaemonClient.ControlSendAsync(started.Id, "text", 0, "ping", socketPath, null, TestContext.Current.CancellationToken);
            Assert.True(seq > 0);

            var messages = await PtyDaemonClient.ControlReadAsync(started.Id, 0, socketPath, null, TestContext.Current.CancellationToken);
            var message = Assert.Single(messages);
            Assert.Equal("text", message.Kind);
            Assert.Equal(0, message.PaneId);
            Assert.Equal("ping", message.Payload);
        }
        finally
        {
            var directory = Path.GetDirectoryName(socketPath)!;
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
