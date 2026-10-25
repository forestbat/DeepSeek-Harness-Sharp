using System.IO.Pipelines;
using System.Text;
using Dsh.Fleet;
using Dsh.Gui.Terminal;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** host.pty.* 交给真实 PTY daemon 后的多端语义: 多个客户端同时 attach 都能看到输出、都能输入, attach 先收到整屏快照, resize, 断开不影响 PTY。 */
[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostPtyTests
{
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoViews_ShareOnePty_SeeEachOthersInput_AndSurviveDisconnect(bool shareClient)
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhp", TestContext.Current.CancellationToken);
        var alice = await host.ConnectAsync("alice", Ct);
        var bob = shareClient ? alice : await host.ConnectAsync("bob", Ct);
        var (fileName, arguments) = RemoteHostMultiClientTests.Shell;
        var ptyId = await alice.Pty.StartPtyAsync(new RemotePtyStartRequest(fileName, arguments, Columns: 100, Rows: 30), Ct);

        await using var aliceView = TerminalCapture.Attach(alice, ptyId);
        await using var bobView = TerminalCapture.Attach(bob, ptyId);
        var (fromAlice, aliceMarker) = MarkerCommand("A", 40, 2);
        await aliceView.SendAsync(fromAlice);
        await aliceView.WaitForAsync(aliceMarker, Arrival);
        await bobView.WaitForAsync(aliceMarker, Arrival);
        var (fromBob, bobMarker) = MarkerCommand("B", 50, 5);
        await bobView.SendAsync(fromBob);
        await bobView.WaitForAsync(bobMarker, Arrival);
        await aliceView.WaitForAsync(bobMarker, Arrival);

        await aliceView.DisposeAsync();
        if (!shareClient)
            await alice.DisposeAsync();
        var (afterDisconnect, afterMarker) = MarkerCommand("C", 60, 6);
        await bobView.SendAsync(afterDisconnect);
        await bobView.WaitForAsync(afterMarker, Arrival);
        var pty = Assert.Single(await PtyDaemonClient.ListAsync(host.PtyEndpoint.SocketPath, host.PtyEndpoint.PortFile, Ct), candidate => candidate.Id == ptyId);
        Assert.Equal(nameof(PtySessionStatus.Running), pty.Status);
        Assert.Equal(HostSessionStatuses.Running, Assert.Single(await bob.ListHostSessionsAsync(Ct), session => session.SessionId == ptyId).Status);
    }

    [Fact(Explicit = true)]
    public async Task Attach_StartsWithAFullScreenSnapshot_AndResizeReachesThePty()
    {
        await using var host = await HarnessHostFixture.CreateAsync("mhq", TestContext.Current.CancellationToken);
        var owner = await host.ConnectAsync("owner", Ct);
        var (fileName, arguments) = RemoteHostMultiClientTests.Shell;
        var ptyId = await owner.Pty.StartPtyAsync(new RemotePtyStartRequest(fileName, arguments, Columns: 100, Rows: 30), Ct);
        await using (var ownerView = TerminalCapture.Attach(owner, ptyId))
        {
            var (command, marker) = MarkerCommand("S", 30, 3);
            await ownerView.SendAsync(command);
            await ownerView.WaitForAsync(marker, Arrival);
        }

        // 后来者在任何新输出之前就看到了此前的屏幕内容: 这只能来自 attach 时的整屏快照。
        var late = await host.ConnectAsync("late", Ct);
        await using var lateView = TerminalCapture.Attach(late, ptyId);
        await lateView.WaitForAsync("S-33", Arrival);

        await late.Pty.ResizePtyAsync(ptyId, 120, 40, Ct);
        var resized = Assert.Single(await PtyDaemonClient.ListAsync(host.PtyEndpoint.SocketPath, host.PtyEndpoint.PortFile, Ct), candidate => candidate.Id == ptyId);
        Assert.Equal((120, 40), (resized.Columns, resized.Rows));
        if (!OperatingSystem.IsWindows())
        {
            await lateView.SendAsync("stty size\n");
            await lateView.WaitForAsync("40 120", Arrival);
        }

        var changes = late.HostSessionChangesAsync(Ct);
        await late.Pty.StopPtyAsync(ptyId, Ct);
        // Throttled activity pushes from the earlier output may still arrive first, so wait for the exit itself.
        await RemoteHostMultiClientTests.NextAsync(changes, change => change.Session?.SessionId == ptyId && change.Session.Status == HostSessionStatuses.Exited);
        await lateView.Completion.WaitAsync(Arrival, Ct);
    }

    [Fact(Explicit = true)]
    public async Task LocalFleet_Terminal_Registry_And_LivePty_Survive_HostRestart_And_OfflineResize()
    {
        if (OperatingSystem.IsWindows())
            return;
        await using var host = await HarnessHostFixture.CreateAsync("mpr", TestContext.Current.CancellationToken);
        await using var machine = new MachineConnection(MachineSpec.Local(host.Root), FleetConnectionTests.TestOptions);
        machine.Start();
        await machine.WaitConnectedAsync(Ct);
        await using var connection = await RemotePtyConnection.StartAsync(machine,
            new RemotePtyStartRequest("/bin/sh", [], host.Root, Title: "persistent-local"), Ct);
        await using var terminal = new TerminalSession(connection, new TerminalSize(80, 24));
        terminal.Start();
        terminal.Send("echo before-$((40+2))\n"u8);
        await WaitForScreenLineAsync(terminal, "before-42");
        var states = FleetConnectionTests.RecordStates(machine);

        await host.StopHostAsync();
        await FleetConnectionTests.WaitForAsync(() => machine.State != MachineConnectionState.Connected);
        terminal.Resize(new TerminalSize(110, 35));
        await connection.ResizeAsync(new TerminalSize(110, 35), Ct);
        await host.StartHostAsync(Ct);
        await FleetConnectionTests.WaitForAsync(() => states.Contains(MachineConnectionState.Reconnecting)
            && machine.State == MachineConnectionState.Connected
            && machine.Sessions.Any(session => session.SessionId == connection.PtyId && session.Status == HostSessionStatuses.Running));
        await using var fresh = await host.ConnectAsync("after-restart", Ct);
        var registered = Assert.Single(await fresh.ListHostSessionsAsync(Ct), session => session.SessionId == connection.PtyId);
        Assert.Equal(HostSessionKinds.Terminal, registered.Kind);
        Assert.Equal("persistent-local", registered.Title);

        terminal.Send("stty size; echo after-$((50+5))\n"u8);
        await WaitForScreenLineAsync(terminal, "35 110");
        await WaitForScreenLineAsync(terminal, "after-55");
        var pty = Assert.Single(await PtyDaemonClient.ListAsync(host.PtyEndpoint.SocketPath, host.PtyEndpoint.PortFile, Ct),
            session => session.Id == connection.PtyId);
        Assert.Equal((110, 35), (pty.Columns, pty.Rows));
        Assert.Equal(nameof(PtySessionStatus.Running), pty.Status);
    }

    private static async Task WaitForScreenLineAsync(TerminalSession terminal, string expected)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(Arrival);
        while (true)
        {
            lock (terminal.Gate)
            {
                var frame = new TerminalFrame();
                frame.Capture(terminal.Screen, 0);
                for (var row = 0; row < frame.Rows; row++)
                {
                    var text = new StringBuilder();
                    foreach (var cell in frame.Row(row))
                        text.Append(cell.Character);
                    if (text.ToString().TrimEnd() == expected)
                        return;
                }
            }
            await Task.Delay(20, deadline.Token);
        }
    }

    /** 只有前台进程真的执行了才会出现的输出(算术结果), 区别于输入回显。 */
    private static (string Input, string Expected) MarkerCommand(string prefix, int left, int right)
        => OperatingSystem.IsWindows()
            ? ($"set /a m={left}+{right} & echo {prefix}-!m!\r", $"{prefix}-{left + right}")
            : ($"echo {prefix}-$(({left}+{right}))\n", $"{prefix}-{left + right}");

    /** 一个客户端对一个远端 PTY 的视图: 输入经管道送进 AttachPtyAsync, 输出累积成文本供断言。 */
    internal sealed class TerminalCapture : IAsyncDisposable
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly StringBuilder _text = new();
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        private Task _attach = Task.CompletedTask;
        private Task _collect = Task.CompletedTask;
        private bool _disposed;

        public static TerminalCapture Attach(RemoteHostClient client, string ptyId)
        {
            var capture = new TerminalCapture();
            capture._attach = Task.Run(() => client.Pty.AttachPtyAsync(ptyId, capture._input.Reader.AsStream(), capture._output.Writer.AsStream(), capture._cts.Token));
            capture._collect = Task.Run(capture.CollectAsync);
            return capture;
        }

        /** attach 正常结束(远端 PTY 退出)时完成。 */
        public Task Completion => _attach;

        public async Task SendAsync(string text)
        {
            await _input.Writer.WriteAsync(Encoding.UTF8.GetBytes(text), Ct);
            await _input.Writer.FlushAsync(Ct);
        }

        public async Task WaitForAsync(string marker, TimeSpan timeout)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(timeout);
            while (!Text.Contains(marker, StringComparison.Ordinal))
            {
                if (deadline.IsCancellationRequested)
                    Assert.Fail($"\"{marker}\" did not arrive; received {Text.Length} chars: {Text[^Math.Min(Text.Length, 400)..]}");
                await Task.Delay(PollInterval, CancellationToken.None);
            }
        }

        private string Text
        {
            get
            {
                lock (_text)
                    return _text.ToString();
            }
        }

        private async Task CollectAsync()
        {
            using var reader = new StreamReader(_output.Reader.AsStream(), Encoding.UTF8);
            var buffer = new char[4096];
            int read;
            while ((read = await reader.ReadAsync(buffer, CancellationToken.None)) > 0)
            {
                lock (_text)
                    _text.Append(buffer, 0, read);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await _cts.CancelAsync();
            await _input.Writer.CompleteAsync();
            try
            {
                await _attach;
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
            }
            await _output.Writer.CompleteAsync();
            await _collect;
            _cts.Dispose();
        }
    }
}


