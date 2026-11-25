using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 已退出的终端会话必须能被删掉, 即使 PTY daemon 已经不记得它: daemon 重启过(它手里的 pty 全没了)、
 * 或者 daemon 干脆没在跑。登记表(host 的 sessions.json)与 daemon 是两份独立状态, 这种"登记表里还在、daemon 里已经没有"
 * 的终端会话在界面上就是一张"已退出"的卡片。
 *
 * 删除因此只该当"已经停了"来处理。失败会从命令处理器抛到 UI 线程, 直接把 GUI 带走
 * (WER 里是 StreamJsonRpc.RemoteInvocation), 宿主进程反而留下来。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class TerminalSessionDeleteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    public async Task Host_DeletesExitedTerminalSession_WhenDaemonNoLongerKnowsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("tsf", ct);
        var client = await host.ConnectAsync("owner", ct);
        var started = await StartExitedTerminalAsync(client, host.Root);
        // daemon 重启后的真实形态: 它不再持有这个 pty, 也没有它的启动记录, 而 host 的登记表里还留着这张卡片。
        await PtyDaemonClient.KillAsync(started.SessionId, host.PtyEndpoint.SocketPath, host.PtyEndpoint.PortFile, ct);
        Assert.Contains(await client.ListHostSessionsAsync(ct), session => session.SessionId == started.SessionId);

        await client.SessionEditing.DeleteSessionAsync(started.SessionId, ct);

        Assert.DoesNotContain(await client.ListHostSessionsAsync(ct), session => session.SessionId == started.SessionId);
    }

    /** 侧边栏"汉堡菜单 → 删除"与仪表盘卡片的删除按钮是同一条命令。 */
    [Theory(Timeout = 120000)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Dashboard_DeleteCommand_RemovesExitedTerminalCard_WhateverTheDaemonThinks(bool daemonGone)
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
                var reports = new List<(string Title, string Detail, bool Failed)>();
                dashboard.ActionReported += (title, detail, failed) => reports.Add((title, detail, failed));
                await UntilAsync(() => dashboard.Find("local", started.SessionId) is not null, "会话卡片出现");
                if (daemonGone)
                    RemoveDaemonEndpoint(host);
                else
                    // daemon 仍在跑, 但已经不认识这个 pty(重启过): 它只回一句 "PTY session not found"。
                    await PtyDaemonClient.KillAsync(started.SessionId, host.PtyEndpoint.SocketPath, host.PtyEndpoint.PortFile, ct);

                // 与侧边栏"汉堡菜单 → 删除"同一条命令; 失败在这里抛出去就会带走 GUI。
                await dashboard.DeleteCommand.ExecuteAsync(dashboard.Find("local", started.SessionId));

                // 卡片消失是后台快照刷新驱动的, 给刷新留一拍; 用户看到的结果以状态报告为准。
                await UntilAsync(() => dashboard.Find("local", started.SessionId) is null, "会话卡片消失");
                Assert.Contains(reports, report => report is { Title: "会话已删除", Failed: false });
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
        var endpoint = host.PtyEndpoint.PortFile ?? host.PtyEndpoint.SocketPath;
        // 客户端与 daemon 都会顺路读这个文件: 撞上它们正好打开时等一拍再删, 而不是当成失败。
        for (var attempt = 0; File.Exists(endpoint); attempt++)
        {
            try
            {
                File.Delete(endpoint);
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
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

