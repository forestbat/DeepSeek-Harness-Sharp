using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Dsh.AgentAdapters;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Notifications;
using Dsh.Pty;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * The GUI workspace (dashboard, quick switcher, notification center, session views) against two real hosts: two independent harness homes act as two machines.
 * Sessions are real: terminal sessions run real shell commands, harness sessions are real agents whose events are appended without calling a model.
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiWorkspaceTests
{
    [Fact]
    public async Task RecentCards_MixMachines_AndProjectFoldSurvivesRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alpha = await HarnessHostFixture.CreateAsync("gwr", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gws", ct);
        var appProject = Project(alpha, "app");
        var serviceProject = Project(beta, "service");
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            try
            {
                await ConnectedAsync(workspace);
                var local = (await workspace.Actions.StartAsync("alpha", Harness(appProject, "本地会话"), ct)).SessionId;
                var remote = (await workspace.Actions.StartAsync("beta", Harness(serviceProject, "远程会话"), ct)).SessionId;
                var dashboard = workspace.Dashboard;
                await UntilAsync(() => dashboard.RecentCards.Count == 2, "recent cards");

                Assert.Contains(dashboard.RecentCards, card => card.SessionId == local && card.Machine == "alpha");
                Assert.Contains(dashboard.RecentCards, card => card.SessionId == remote && card.Machine == "beta");
                var project = dashboard.Machines.Single(machine => machine.Name == "alpha").Projects.Single();
                project.ToggleCommand.Execute(null);
                Assert.False(project.IsExpanded);
                dashboard.Refresh();
                Assert.False(project.IsExpanded);
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(5);

    /** Fast heartbeat and backoff, so a stopped host is noticed and the reconnect happens within the test. */
    private static readonly FleetOptions Options = FleetConnectionTests.TestOptions with { ClientName = "gui-workspace-test" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 5000)]
    public async Task Dashboard_GroupsBothMachines_FiltersSearchesSorts_AndPaletteJumps()
    {
        var ct = TestContext.Current.CancellationToken;
        SkipOnWindows();
        await using var alpha = await HarnessHostFixture.CreateAsync("gwa", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gwb", ct);
        var appProject = Project(alpha, "app");
        var serviceProject = Project(beta, "service");
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            var destinations = new List<WorkspaceDestination>();
            workspace.NavigationRequested += destinations.Add;
            try
            {
                await ConnectedAsync(workspace);
                var waiting = (await workspace.Actions.StartAsync("alpha", Harness(appProject, "deploy 等待确认"), ct)).SessionId;
                var approval = RemoteHostMultiClientTests.RaiseApprovalAsync(alpha, RemoteHostMultiClientTests.Agent(alpha, waiting), "deploy");
                var busy = await StartTerminalAsync(workspace, "alpha", appProject, "build 长任务", "sleep 30");
                var done = await StartTerminalAsync(workspace, "beta", serviceProject, "构建完成", "exit 0");
                var dashboard = workspace.Dashboard;
                await UntilAsync(() => Card(workspace, "alpha", waiting)?.NeedsAttention == true
                    && Card(workspace, "alpha", busy)?.IsRunning == true
                    && Card(workspace, "beta", done)?.IsExited == true, "session states");

                Assert.Equal(["alpha", "beta"], dashboard.Machines.Select(machine => machine.Name));
                Assert.All(dashboard.Machines, machine => Assert.True(machine.IsConnected && machine.VersionLabel.Length > 0));
                var alphaProjects = Assert.Single(dashboard.Machines[0].Projects);
                Assert.Equal(appProject, alphaProjects.ProjectPath);
                Assert.Equal(new[] { waiting, busy }.Order(), alphaProjects.Sessions.Select(card => card.SessionId).Order());
                Assert.Equal(serviceProject, Assert.Single(dashboard.Machines[1].Projects).ProjectPath);
                Assert.Equal((3, 1, 1, 0), (dashboard.SessionCount, dashboard.AttentionCount, dashboard.RunningCount, dashboard.OfflineMachineCount));
                Assert.Equal(3, dashboard.RecentCards.Count);
                Assert.All(dashboard.RecentCards, card => Assert.Contains(card.SessionId, new[] { waiting, busy, done }));
                alphaProjects.ToggleCommand.Execute(null);
                Assert.False(alphaProjects.IsExpanded);
                dashboard.Refresh();
                Assert.False(alphaProjects.IsExpanded);
                alphaProjects.ToggleCommand.Execute(null);
                Assert.True(alphaProjects.IsExpanded);
                Assert.Equal("等待你处理", Card(workspace, "alpha", waiting)!.StatusLabel);

                dashboard.ShowAttentionCommand.Execute(null);
                Assert.Equal([waiting], VisibleSessions(dashboard));
                dashboard.Filter = DashboardFilter.Exited;
                Assert.Equal([done], VisibleSessions(dashboard));
                dashboard.Filter = DashboardFilter.All;
                dashboard.SearchText = "长任务";
                Assert.Equal([busy], VisibleSessions(dashboard));
                dashboard.SearchText = "";
                dashboard.Sort = DashboardSort.Status;
                Assert.Equal([waiting, busy], alphaProjects.Sessions.Select(card => card.SessionId));
                dashboard.Sort = DashboardSort.Title;
                Assert.Equal([busy, waiting], alphaProjects.Sessions.Select(card => card.SessionId));

                var palette = workspace.Palette;
                palette.ToggleCommand.Execute(null);
                Assert.Equal(waiting, palette.Results[0].SessionId);
                palette.Query = "beta 构建";
                Assert.Equal(done, Assert.Single(palette.Results).SessionId);
                palette.Query = "chrw";
                Assert.Empty(palette.Results);
                palette.Query = "长务";
                palette.ConfirmCommand.Execute(null);
                Assert.False(palette.IsOpen);
                Assert.Equal(busy, workspace.ActiveSession?.SessionId);
                Assert.Equal(WorkspaceDestination.Session, destinations[^1]);

                workspace.ShowDashboard();
                Assert.Null(workspace.ActiveSession);
                Assert.Equal(DashboardSort.Title, dashboard.Sort);
                await Assert.Single(workspace.Notifications.Pending).RejectCommand.ExecuteAsync(null);
                Assert.Equal(ApprovalOutcome.Rejected, await approval.WaitAsync(Arrival, ct));
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact(Timeout = 5000)]
    public async Task NotificationCenter_SyncsReadMarks_AnswersPendingOnce_AndNotifiesDesktop()
    {
        var ct = TestContext.Current.CancellationToken;
        SkipOnWindows();
        await using var alpha = await HarnessHostFixture.CreateAsync("gna", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gnb", ct);
        var betaProject = Project(beta, "web");
        var bob = await alpha.ConnectAsync("bob", ct);
        var carol = await beta.ConnectAsync("carol", ct);
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            var notifications = workspace.Notifications;
            var desktop = new List<string>();
            var toasts = new List<string>();
            notifications.IsForeground = () => false;
            notifications.DesktopNotify = (title, _) => desktop.Add(title);
            notifications.Toast = (title, _, _) => toasts.Add(title);
            try
            {
                await ConnectedAsync(workspace);
                var approval = RemoteHostMultiClientTests.RaiseApprovalAsync(alpha, alpha.Environment.Agent, "bash");
                var waiting = (await workspace.Actions.StartAsync("beta", Harness(betaProject, "等待审批"), ct)).SessionId;
                var betaApproval = RemoteHostMultiClientTests.RaiseApprovalAsync(beta, RemoteHostMultiClientTests.Agent(beta, waiting), "deploy");
                await UntilAsync(() => notifications.Pending.Count == 2
                    && notifications.Items.Any(item => item is { Machine: "alpha", Kind: NotificationKind.ApprovalRequest })
                    && notifications.Items.Any(item => item is { Machine: "beta", Kind: NotificationKind.ApprovalRequest }), "structured approval notifications");
                Assert.True(notifications.UnreadTotal >= 2);
                Assert.Contains(desktop, title => title.StartsWith("alpha · ", StringComparison.Ordinal));
                Assert.Contains(desktop, title => title.StartsWith("beta · ", StringComparison.Ordinal));
                Assert.Equal(notifications.Items.OrderByDescending(item => item.CreatedAt), notifications.Items);

                notifications.SelectedMachine = notifications.MachineOptions.Single(option => option.Value == "beta");
                Assert.All(notifications.Items, item => Assert.Equal("beta", item.Machine));
                notifications.SelectedMachine = notifications.MachineOptions[0];
                notifications.SelectedKind = notifications.KindOptions.Single(option => option.Value == nameof(NotificationKind.ApprovalRequest));
                Assert.All(notifications.Items, item => Assert.Equal(NotificationKind.ApprovalRequest, item.Kind));
                notifications.SelectedKind = notifications.KindOptions[0];

                // Another client of alpha marks everything read: this client's marks and counts follow.
                var alphaUnread = workspace.Fleet.Find("alpha")!.UnreadNotifications;
                Assert.True(alphaUnread > 0);
                await bob.Notifications.MarkNotificationsReadAsync(new RemoteNotifyMarkRead(All: true), ct);
                await UntilAsync(() => workspace.Fleet.Find("alpha")!.UnreadNotifications == 0
                    && notifications.Items.Where(item => item.Machine == "alpha").All(item => item.IsRead), "read marks from another client");

                // Marking one read here reaches the host (and every other client).
                var betaItem = notifications.Items.First(item => item is { Machine: "beta", IsUnread: true });
                await notifications.MarkReadCommand.ExecuteAsync(betaItem);
                var betaPage = await carol.Notifications.ListNotificationsAsync(cancellationToken: ct);
                Assert.True(betaPage.Items.Single(record => record.Id == betaItem.Id).IsRead);

                // The first answer wins: bob answers, the request leaves this client's list, and answering it here is refused.
                var pending = Assert.Single(notifications.Pending, item => item.Machine == "alpha");
                Assert.Equal(("alpha", "bash"), (pending.Machine, pending.Item.Approval!.Tool));
                var seen = Assert.Single((await bob.ListPendingAsync(ct)).Approvals);
                Assert.True(await bob.RespondApprovalAsync(seen.RequestId, true, cancellationToken: ct));
                Assert.Equal(ApprovalOutcome.AllowedOnce, await approval.WaitAsync(Arrival, ct));
                await UntilAsync(() => notifications.Pending.Count == 1 && notifications.Pending.All(item => item.Machine == "beta"), "resolution removes the request");
                await pending.AllowCommand.ExecuteAsync(null);
                Assert.Contains("请求已被处理", toasts);

                // Opening a notification jumps to its session.
                var waitingItem = notifications.Items.First(item => item.SessionId == waiting);
                await notifications.OpenCommand.ExecuteAsync(waitingItem);
                Assert.Equal(waiting, workspace.ActiveSession?.SessionId);
                Assert.True(waitingItem.IsRead);
                await Assert.Single(notifications.Pending).RejectCommand.ExecuteAsync(null);
                Assert.Equal(ApprovalOutcome.Rejected, await betaApproval.WaitAsync(Arrival, ct));
                await UntilAsync(() => notifications.Pending.Count == 0, "all real pending requests resolved");
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact(Timeout = 5000)]
    public async Task PendingApproval_AnsweredFromTheSessionHeader_ReachesTheHost()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alpha = await HarnessHostFixture.CreateAsync("gpa", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gpb", ct);
        var project = Project(alpha, "repo");
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            try
            {
                await ConnectedAsync(workspace);
                var started = await workspace.Actions.StartAsync("alpha", Harness(project, "审批会话"), ct);
                var agent = RemoteHostMultiClientTests.Agent(alpha, started.SessionId);
                var approval = RemoteHostMultiClientTests.RaiseApprovalAsync(alpha, agent, "write");
                await UntilAsync(() => Card(workspace, "alpha", started.SessionId) is { HasPending: true, NeedsAttention: true }, "pending count on the card");
                workspace.OpenSession("alpha", started.SessionId);
                var session = workspace.ActiveSession!;
                await UntilAsync(() => session.HasPending, "pending request in the session header");

                await Assert.Single(session.Pending).RejectCommand.ExecuteAsync(null);

                Assert.Equal(ApprovalOutcome.Rejected, await approval.WaitAsync(Arrival, ct));
                await UntilAsync(() => !session.HasPending && workspace.Notifications.Pending.Count == 0, "the answered request disappears");
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact(Timeout = 5000)]
    public async Task HarnessSession_OpensMainChatView_InsteadOfSessionTranscript()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var alpha = await HarnessHostFixture.CreateAsync("gha", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("ghb", ct);
        var project = Project(beta, "notes");
        await HeadlessGui.RunAsync(async () =>
        {
            var workspace = Open(alpha, beta);
            try
            {
                await ConnectedAsync(workspace);
                var started = await workspace.Actions.StartAsync("beta", Harness(project, "整理笔记"), ct);
                await UntilAsync(() => Card(workspace, "beta", started.SessionId) is not null, "the harness session's card");
                (string Machine, string SessionId)? requested = null;
                workspace.ChatRequested += (machine, id) => requested = (machine, id);
                workspace.OpenSession("beta", started.SessionId);
                Assert.Equal(("beta", started.SessionId), requested);
                Assert.Null(workspace.ActiveSession);
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

/** The test page is a plain TextBlock: it stands in for a module's page and only checks the navigation contract. */
    [Fact]
    public async Task WorkspacePages_ShowNothingUntilRegistered_ThenOpenWithTheWorkspaceContext()
    {
        await using var gui = await GuiTestEnvironment.CreateAsync();
        await HeadlessGui.RunAsync(async () =>
        {
            var fleet = new FleetWorkspace(new MachineStore(gui.App.Home), Options, () => new FleetClient([], Options));
            var workspace = new WorkspaceViewModel(fleet,
        (_, _, _) => { });
            var destinations = new List<WorkspaceDestination>();
            workspace.NavigationRequested += destinations.Add;
            try
            {
                Assert.Empty(workspace.Pages.Available);
                WorkspacePageContext? opened = null;
                var page = new WorkspacePageEntry(WorkspacePages.Git, "Git", Geometry.Parse("M0,0 L1,1"), context =>
                {
                    opened = context;
                    return new Avalonia.Controls.TextBlock { Text = "git page" };
                });
                workspace.Pages.Register(page);
                workspace.Pages.Register(page);
                Assert.Same(page, Assert.Single(workspace.Pages.Available));

                workspace.OpenPage(page);

                Assert.Equal((null, null), (opened?.Machine, opened?.ProjectPath));
                Assert.Equal("git page", Assert.IsType<Avalonia.Controls.TextBlock>(workspace.PageContent).Text);
                Assert.Equal((page, WorkspaceDestination.Page), (workspace.ActivePage, destinations[^1]));
                workspace.ShowDashboard();
                Assert.Equal((null, null), (workspace.PageContent, workspace.ActivePage));
            }
            finally
            {
                await workspace.DisposeAsync();
            }
        });
    }

    [Fact]
    public void ParseCommand_SplitsWords_AndKeepsQuotedSpaces()
    {
        var command = NewSessionViewModel.ParseCommand("""  my-agent --name "two words" 'it''s' a\ b {prompt}  """);
        Assert.Equal("my-agent", command.Executable);
        Assert.Equal(["--name", "two words", "its", "a b", "{prompt}"], command.Arguments);
        Assert.Throws<ArgumentException>(() => NewSessionViewModel.ParseCommand("   "));
    }

    [Fact(Timeout = 5000)]
    public async Task NewSession_OpenedBeforeConnection_EnablesTheSelectedMachineWithoutReselection_AndCancelsClosedForms()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("gnc", ct);
        await HeadlessGui.RunAsync(async () =>
        {
            var fleet = new FleetWorkspace(new MachineStore(new HarnessHome(host.Root)), Options,
                () => new FleetClient([MachineSpec.Local(host.Root, "alpha"), MachineSpec.Local(host.Root, "beta")], Options));
        var actions = new FleetSessionActions(fleet);
            var form = new NewSessionViewModel(fleet, actions, "alpha", host.Root);
            var window = new NewSessionWindow(form);
            var closedForm = new NewSessionViewModel(fleet, actions, "beta", host.Root);
            var closedWindow = new NewSessionWindow(closedForm);
            try
            {
                window.Show();
                closedWindow.Show();
                form.Machine = "beta";
                Assert.Null(form.Agent);
                Assert.False(form.StartCommand.CanExecute(null));
                closedWindow.Close();
                fleet.Start();
                await UntilAsync(() => form.Agent is { IsAvailable: true } && form.StartCommand.CanExecute(null),
                    "the selected machine's catalog after its real connection completes");
                Assert.Equal("beta", form.Machine);
                Assert.Empty(closedForm.Agents);
                Assert.Null(closedForm.Agent);
                window.Close();
                var lease = await fleet.Find("beta")!.WaitConnectedAsync(ct);
                Assert.True(await lease.Client.PingAsync(ct) > 0);
                var previousMachine = await fleet.Find("alpha")!.WaitConnectedAsync(ct);
                Assert.True(await previousMachine.Client.PingAsync(ct) > 0);
            }
            finally
            {
                closedWindow.Close();
                window.Close();
                await fleet.DisposeAsync();
            }
        });
    }

    /**
     * Files, Git and machine management as workspace pages: files and Git open on the machine and project of the session in view and are rebuilt on the next link after the host restarts.
     * A Git worktree opens the new-session form there; saving a machine in machine management reconnects the workspace while the page stays in view.
     */
    [Fact(Timeout = 5000)]
    public async Task ContributedPages_FollowTheSessionAndTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        SkipOnWindows();
        using var repo = new GitTestRepository();
        repo.Commit(("readme.md", "hello\n"));
        await using var alpha = await HarnessHostFixture.CreateAsync("gpa", ct);
        await using var beta = await HarnessHostFixture.CreateAsync("gpb", ct);
        await HeadlessGui.RunAsync(async () =>
        {
            var store = new MachineStore(new HarnessHome(alpha.Root));
            var fleet = new FleetWorkspace(
                store, Options, () => new FleetClient([MachineSpec.Local(alpha.Root, "alpha"), MachineSpec.Local(beta.Root, "beta"), .. store.Load().Machines], Options));
        var workspace = new WorkspaceViewModel(fleet, (_, _, _) => { });
            workspace.RegisterHostPages(Icon("Icon.Folder"), Icon("Icon.Git"), Icon("Icon.Machine"));
            NewSessionViewModel? form = null;
            workspace.NewSessionDialogRequested += requested => form = requested;
            workspace.Start();
            try
            {
                await ConnectedAsync(workspace);
                Assert.Equal([WorkspacePages.Files, WorkspacePages.Git, WorkspacePages.Machines], workspace.Pages.Available.Select(page => page.Id));
                var session = await StartTerminalAsync(workspace, "alpha", repo.Root, "repo shell", "sleep 30");
                await UntilAsync(() => Card(workspace, "alpha", session) is not null, "the session card");
                workspace.OpenSession("alpha", session);

                workspace.OpenPage(Page(workspace, WorkspacePages.Files));
                var files = Assert.IsType<HostPageViewModel>(workspace.PageContent);
                Assert.Equal(("alpha", repo.Root), (files.Machine, files.Path));
                await UntilAsync(() => files.Content is FilesViewModel { Roots: [{ Path: var root }] } && root == repo.Root, "files on the session's project");
                var firstLink = files.Content;
                await alpha.RestartHostAsync(ct);
                await UntilAsync(() => files.Content is FilesViewModel { Roots.Count: 1 } rebuilt && !ReferenceEquals(rebuilt, firstLink), "files rebuilt on the new link");

                workspace.OpenPage(Page(workspace, WorkspacePages.Git));
                Assert.Null(files.Content);
                var git = Assert.IsType<HostPageViewModel>(workspace.PageContent);
                await UntilAsync(() => git.Content is GitViewModel { Worktrees.Count: > 0 }, "the repository's worktrees");
                var repository = (GitViewModel)git.Content!;
                await repository.RequestSessionCommand.ExecuteAsync(repository.Worktrees[0]);
                Assert.Equal(("alpha", repository.Worktrees[0].Path), (form?.Machine, form?.ProjectPath));

                workspace.OpenPage(Page(workspace, WorkspacePages.Machines));
                var machines = workspace.Machines;
                Assert.Same(machines, workspace.PageContent);
                machines.NewLocalCommand.Execute(null);
                (machines.Editor!.Name, machines.Editor.HarnessHome) = ("gamma", beta.Root);
                await machines.SaveCommand.ExecuteAsync(null);
                var connected = MachineRowViewModel.StateLabel(MachineConnectionState.Connected);
                await UntilAsync(() => machines.Machines.FirstOrDefault(row => row.Name == "gamma")?.StateText == connected, "machine management shows the new client");
                Assert.Equal(MachineConnectionState.Connected, workspace.Fleet.Find("gamma")!.State);
                Assert.Same(machines, workspace.PageContent);
            }
            finally
            {
                form?.Dispose();
                await workspace.DisposeAsync();
            }
        });
    }

    private static Geometry Icon(string key) => (Geometry)Avalonia.Controls.ResourceNodeExtensions.FindResource(Application.Current!, key)!;

    /** Gates only the stop RPC while all Host and PTY behavior is delegated to the real backend. */
private static WorkspacePageEntry Page(WorkspaceViewModel workspace, string id) => workspace.Pages.Available.Single(page => page.Id == id);

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("terminal sessions run POSIX shell commands");
    }

    /** The workspace over both hosts, with the machines named as the dashboard shows them. */
    private static WorkspaceViewModel Open(HarnessHostFixture alpha, HarnessHostFixture beta)
    {
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(alpha.Root, "host.sock"));
        TempTree.AssertFitsMacOsSocketLimit(Path.Combine(beta.Root, "host.sock"));
        var fleet = new FleetWorkspace(
            new MachineStore(new HarnessHome(alpha.Root)), Options, () => new FleetClient([MachineSpec.Local(alpha.Root, "alpha"), MachineSpec.Local(beta.Root, "beta")], Options));
        var workspace = new WorkspaceViewModel(fleet, (_, _, _) => { });
        workspace.Start();
        return workspace;
    }

    private static async Task ConnectedAsync(WorkspaceViewModel workspace)
    {
        foreach (var machine in workspace.Fleet.Client.Machines)
            await machine.WaitConnectedAsync(Ct);
        await UntilAsync(() => workspace.Dashboard.Machines.All(machine => machine.IsConnected), "both machines connected");
    }

    private static string Project(HarnessHostFixture host, string name)
        => Directory.CreateDirectory(Path.Combine(host.Root, name)).FullName;

    private static async Task<string> StartTerminalAsync(
        WorkspaceViewModel workspace, string machine, string project, string title, string script)
    {
        var request = new RemoteAgentStartRequest(
            AgentKinds.Terminal, project, Command: new RemoteAgentCommand("/bin/sh", ["-c", script]), Title: title);
        return (await workspace.Actions.StartAsync(machine, request, Ct)).SessionId;
    }

    private static RemoteAgentStartRequest Harness(string project, string title) => new(HostAgentKinds.Dsharp, project, Title: title);

    private static SessionCardViewModel? Card(WorkspaceViewModel workspace, string machine, string sessionId)
        => workspace.Dashboard.Find(machine, sessionId);

    private static List<string> VisibleSessions(DashboardViewModel dashboard)
        => [.. dashboard.Machines.SelectMany(machine => machine.Projects).SelectMany(project => project.Sessions).Select(card => card.SessionId)];

    /** The visible screen of a terminal tab, row by row. */
    private static string ScreenText(TerminalViewModel? terminal)
    {
        if (terminal?.Session is not { } session)
            return "";
        lock (session.Gate)
        {
            var screen = session.Screen;
            var rows = new List<string>();
            for (var y = 0; y < screen.Height; y++)
            {
                var row = screen.Row(y);
                var characters = new char[row.Length];
                for (var x = 0; x < row.Length; x++)
                    characters[x] = row[x].Character;
                rows.Add(new string(characters).TrimEnd());
            }
            return string.Join('\n', rows);
        }
    }

    /** Polls on the UI thread, running the posted UI work between checks. */
    internal static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Arrival;
        while (true)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out waiting for {what}");
            await Task.Delay(50, Ct);
        }
    }
}
