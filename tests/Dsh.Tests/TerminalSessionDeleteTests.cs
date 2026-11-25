using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 已退出的终端会话必须能被删掉, 而且删的时候 PTY daemon 可能已经不在了(重启、或用户关掉了 daemon)。
 * 那时 daemon 手里的 PTY 也随之消失: 删除只该当"已经停了"来处理, 不能因为连不上 daemon 就失败 —— 失败会从
 * 命令处理器抛到 UI 线程, 直接把 GUI 带走(WER 里是 StreamJsonRpc.RemoteInvocation)。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class TerminalSessionDeleteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static readonly FleetOptions Options = FleetConnectionTests.TestOptions with { ClientName = "terminal-delete-test" };

    [Fact(Timeout = 120000)]
    public async Task Host_DeletesExitedTerminalSession_WhenPtyDaemonIsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("tsd", ct);
        var client = await host.ConnectAsync("owner", ct);
        var started = await StartExitedTerminalAsync(client, host.Root);
        RemoveDaemonEndpoint(host);

        await client.SessionEditing.DeleteSessionAsync(started.SessionId, ct);

        Assert.DoesNotContain(await client.ListHostSessionsAsync(ct), session => session.SessionId == started.SessionId);
        // 启动记录也要跟着消失: 留着的话 attach 会按记录把"已删除"的会话重新拉起来。
        Assert.Null(PtyRecords.TryLoad(started.SessionId, DaemonRunRoot(host)));
    }

    [Fact(Timeout = 120000)]
    public async Task Dashboard_DeleteCommand_RemovesExitedTerminalCard_WhenPtyDaemonIsGone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("tsg", ct);
        var project = Directory.CreateDirectory(Path.Combine(host.Root, "proj")).FullName;
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(host);
            try
            {
                foreach (var machine in workspace.Fleet.Client.Machines)
                    await machine.WaitConnectedAsync(ct);
                var started = await workspace.Actions.StartAsync(
                    "local",
                    new RemoteAgentStartRequest(
                        AgentKinds.Terminal, project,
                        Command: new RemoteAgentCommand(ExitingShell.FileName, [.. ExitingShell.Arguments]),
                        Title: "已退出的终端"),
                    ct);
                var dashboard = workspace.Dashboard;
                await UntilAsync(() => dashboard.Find("local", started.SessionId) is not null, "会话卡片出现");
                RemoveDaemonEndpoint(host);

                // 与侧边栏"汉堡菜单 → 删除"同一条命令; 失败在这里抛出去就会带走 GUI。
                await dashboard.DeleteCommand.ExecuteAsync(dashboard.Find("local", started.SessionId));

                Assert.Null(dashboard.Find("local", started.SessionId));
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    /** 起一个立刻退出的终端会话(agent.kind = terminal, 与 GUI"新建终端"同一条路径)。 */
    private static async Task<RemoteSessionSummary> StartExitedTerminalAsync(RemoteHostClient client, string root)
    {
        var (fileName, arguments) = ExitingShell;
        var project = Directory.CreateDirectory(Path.Combine(root, "proj")).FullName;
        var started = await client.Agent.StartAgentAsync(
            new RemoteAgentStartRequest(
                AgentKinds.Terminal, project,
                Command: new RemoteAgentCommand(fileName, [.. arguments, "exit"]),
                Title: "已退出的终端"),
            Ct);
        for (var i = 0; i < 100; i++)
        {
            var entry = (await client.ListHostSessionsAsync(Ct)).FirstOrDefault(session => session.SessionId == started.SessionId);
            if (entry is null || entry.Status == HostSessionStatuses.Exited)
                return started;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException("the terminal session never reported its exit");
    }

    /** 一个立刻退出的 shell: 会话起完就是"已退出"的终端会话。 */
    private static (string FileName, string[] Arguments) ExitingShell
        => OperatingSystem.IsWindows() ? ("cmd.exe", ["/c", "exit"]) : ("/bin/sh", ["-c", "exit"]);

    /** daemon 不在了的真实形态: 端点文件消失(重启后、或用户关掉 daemon)。 */
    private static void RemoveDaemonEndpoint(HarnessHostFixture host)
    {
        if (host.PtyEndpoint.PortFile is { } portFile && File.Exists(portFile))
            File.Delete(portFile);
        else if (File.Exists(host.PtyEndpoint.SocketPath))
            File.Delete(host.PtyEndpoint.SocketPath);
    }

    /** 记录文件在 daemon 端点旁边(与 PtyDaemon 的 _runRoot 同一算法)。 */
    private static string DaemonRunRoot(HarnessHostFixture host)
        => Path.GetDirectoryName(host.PtyEndpoint.PortFile ?? host.PtyEndpoint.SocketPath)!;

    private static WorkspaceViewModel Open(HarnessHostFixture host)
    {
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(host.Root, "host.sock"));
        var fleet = new FleetWorkspace(new FleetWorkspaceServices(
            new MachineStore(new HarnessHome(host.Root)), Options,
            () => new FleetClient([MachineSpec.Local(host.Root, "local")], Options)));
        var workspace = new WorkspaceViewModel(fleet, (_, _, _) => { });
        workspace.Start();
        return workspace;
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            if (Dispatcher.UIThread.CheckAccess() ? condition() : await Dispatcher.UIThread.InvokeAsync(condition))
                return;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"等待 {what} 超时");
    }
}



