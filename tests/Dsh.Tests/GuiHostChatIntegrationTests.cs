using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** Ordinary Chat uses the same authenticated Host transport as other clients, not a GUI AgentRegistry. */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiHostChatIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrdinaryChat_CreatesOnlyOnHost_AndBroadcastsComposerImagesAndCommands()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gc", TestContext.Current.CancellationToken);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var observer = await host.ConnectAsync("other-client", Ct);
        await using var fleet = Fleet(host, gui);
        var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Ct);
        for (var attempt = 0; attempt < 100 && !chat.Commands.Any(command => command.Name == "goal"); attempt++)
        {
            await chat.RefreshCommandsAsync();
            if (!chat.Commands.Any(command => command.Name == "goal"))
                await Task.Delay(50, Ct);
        }
        Assert.Contains(chat.Commands, command => command.Name == "goal");
        await HeadlessGui.RunAsync(async () =>
        {
            var agents = gui.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
            var before = agents.List().Count;
            await using var viewModel = new MainViewModel(gui.App, chat);
            var id = viewModel.CurrentSessionId.Value;
            Assert.Equal(before, agents.List().Count);
            Assert.Contains(await observer.ListHostSessionsAsync(Ct), session => session.SessionId == id && session.Kind == HostSessionKinds.Harness);

            viewModel.Composer.Input = "/goal";
            await GuiTestEnvironment.PumpAsync(viewModel.SubmitCommand.ExecuteAsync(null));
            await GuiTestEnvironment.WaitForAsync(viewModel,
                () => viewModel.Messages.Any(message => message.Kind == MessageKind.System && message.Text.Contains("no current goal", StringComparison.Ordinal)));
            var snapshot = await observer.GetSessionSnapshotAsync(id, Ct);
            Assert.Contains(snapshot.Events, item => item.Type == "command/run");
            Assert.Contains(snapshot.Events, item => item.Type == "command/done");

            var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2, 3 };
            viewModel.AttachImage("clipboard.png", png, 3, 2);
            viewModel.Composer.Input = "ordinary local chat";
            await GuiTestEnvironment.PumpAsync(viewModel.SubmitCommand.ExecuteAsync(null));
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                () => viewModel.Messages.Any(message => message.Kind == MessageKind.User && message.Text.Contains("ordinary local chat", StringComparison.Ordinal)),
                () => GuiTestEnvironment.DescribeChatState(viewModel));
            Assert.Single(viewModel.Messages, message => message.Kind == MessageKind.User && message.Text.Contains("ordinary local chat", StringComparison.Ordinal));
            Assert.Empty(viewModel.Attachments);
            snapshot = await observer.GetSessionSnapshotAsync(id, Ct);
            var user = Assert.Single(snapshot.Events.Select(item => item.Data)
                .OfType<UserMessagePayload>(), item => item.Message.Content.OfType<Dsh.Llm.TextBlock>().Any(text => text.Text == "ordinary local chat"));
            Assert.Contains(user.Message.Content, block => block is ImageBlock { Attachment.Width: 3, Attachment.Height: 2 });
            Assert.Equal(before, agents.List().Count);
        });
    }

    [Fact]
    public async Task WorkspaceHarnessCard_SwitchesTheMainChatShell()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gw", TestContext.Current.CancellationToken);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Root, null, Ct);
            await using var viewModel = new MainViewModel(gui.App, chat);
            var started = await viewModel.Workspace.Actions.StartAsync(
                "local",
                new RemoteAgentStartRequest(HostAgentKinds.Dsharp, host.Root, Title: "main-chat-route"),
                Ct);
            await GuiTestEnvironment.WaitForAsync(viewModel.Workspace.Dashboard,
                () => viewModel.Workspace.Dashboard.Find("local", started.SessionId) is not null);

            viewModel.Workspace.OpenSession("local", started.SessionId);
            await GuiTestEnvironment.WaitForAsync(viewModel,
                () => viewModel.CurrentSessionId.Value == started.SessionId && viewModel.Page == AppPage.Chat);
            Assert.Null(viewModel.Workspace.ActiveSession);
        });
    }

    [Fact]
    public async Task ClosingGui_DetachesOnly_AndPendingTaskCanBeAnsweredByAnotherClient()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gl", TestContext.Current.CancellationToken);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var observer = await host.ConnectAsync("survivor", Ct);
        string id = "";
        Task<ApprovalOutcome>? waiting = null;
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Ct);
            await using (var viewModel = new MainViewModel(gui.App, chat))
            {
                id = viewModel.CurrentSessionId.Value;
                var agent = (AgentLoopAgent)host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(SessionId.Create(id))!;
                // Host work must not capture the GUI dispatcher that this test deliberately detaches.
                waiting = Task.Run(() => RemoteHostMultiClientTests.RaiseApprovalAsync(host, agent, "bash"), Ct);
                await GuiTestEnvironment.WaitForAsync(viewModel.Workspace.Notifications.Pending,
                    () => viewModel.Workspace.Notifications.Pending.Any(item => item.SessionId == id));
            }
            Dispatcher.UIThread.RunJobs();
        });
        Assert.NotNull(waiting);
        Assert.False(waiting.IsCompleted);
        var request = Assert.Single((await observer.ListPendingAsync(Ct)).Approvals, item => item.SessionId == id);
        Assert.True(await observer.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct));
        Assert.Equal(ApprovalOutcome.AllowedOnce, await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Contains(await observer.ListHostSessionsAsync(Ct), session => session.SessionId == id && session.Status != HostSessionStatuses.Exited);
        await observer.Agent.StopAgentAsync(id, Ct);
        Assert.Contains(await observer.ListHostSessionsAsync(Ct), session => session.SessionId == id && session.Status == HostSessionStatuses.Exited);
    }

    [Fact]
    public async Task PersistedPreFleetChat_IsResumedByHostWithSameIdentityAndFullTranscript()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gm", TestContext.Current.CancellationToken);
        var legacy = host.Environment.Agent.Session;
        var id = legacy.Id.Value;
        legacy.Append(new UserMessagePayload(MessageFactory.CreateUserText("persisted before fleet")), new SurfaceOp.Append());
        await host.Environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(legacy);
        await host.RestartHostAsync(Ct);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var observer = await host.ConnectAsync("migration-observer", Ct);
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, id, Ct);
            await using var viewModel = new MainViewModel(gui.App, chat);
            Assert.Equal(id, viewModel.CurrentSessionId.Value);
            Assert.Contains(viewModel.Messages, message => message.Kind == MessageKind.User && message.Text == "persisted before fleet");
            Assert.Contains(await observer.ListHostSessionsAsync(Ct), session => session.SessionId == id);
            Assert.DoesNotContain(gui.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.List(), agent => agent.Id.Value == id);
        });
    }

    [Fact]
    public async Task HostTruncation_RebuildsRichChatProjection_WithoutKeepingDiscardedMessages()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gr", TestContext.Current.CancellationToken);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Ct);
            await using var viewModel = new MainViewModel(gui.App, chat);
            var agent = host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(viewModel.CurrentSessionId)!;
            var keepCount = agent.Session.Seq;
            agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("discarded tail")), new SurfaceOp.Append());
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Any(message => message.Text == "discarded tail"));
            Assert.True(agent.Session.TryTruncateTo(keepCount));
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.All(message => message.Text != "discarded tail"));
            agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("replacement at reused seq")), new SurfaceOp.Append());
            await GuiTestEnvironment.WaitForAsync(viewModel.Messages, () => viewModel.Messages.Any(message => message.Text == "replacement at reused seq"));
            Assert.Single(viewModel.Messages, message => message.Text == "replacement at reused seq");
            Assert.DoesNotContain(viewModel.Messages, message => message.Text == "discarded tail");
        });
    }

    [Fact]
    public async Task AnotherClientAnswer_ClosesOrdinaryChatDecisionDialog_AndWinsOnce()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gd", TestContext.Current.CancellationToken);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var observer = await host.ConnectAsync("answering-client", Ct);
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Ct);
            await using var viewModel = new MainViewModel(gui.App, chat);
            var shown = new TaskCompletionSource<DecisionViewModel>(TaskCreationOptions.RunContinuationsAsynchronously);
            var closed = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            viewModel.DecisionRequested += decision =>
            {
                decision.Closed += value => closed.TrySetResult(value.Result);
                shown.TrySetResult(decision);
                return closed.Task;
            };
            var agent = host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(viewModel.CurrentSessionId)!;
            var waiting = RemoteHostMultiClientTests.RaiseApprovalAsync(host, agent, "bash");
            var dialog = await GuiTestEnvironment.PumpAsync(shown.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.True(dialog.IsApproval);
            var pending = await RemoteHostMultiClientTests.NextAsync(observer.PendingChangesAsync(Ct), change => change.Pending.Approvals.Any(item => item.SessionId == viewModel.CurrentSessionId.Value));
            var request = Assert.Single(pending.Pending.Approvals, item => item.SessionId == viewModel.CurrentSessionId.Value);
            Assert.True(await observer.RespondApprovalAsync(request.RequestId, true, cancellationToken: Ct));
            Assert.Equal(ApprovalOutcome.Cancelled, await GuiTestEnvironment.PumpAsync(closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)));
            Assert.Equal(ApprovalOutcome.AllowedOnce, await waiting.WaitAsync(TimeSpan.FromSeconds(5), Ct));
            Assert.False(await observer.RespondApprovalAsync(request.RequestId, false, cancellationToken: Ct));
            var change = await RemoteHostMultiClientTests.NextAsync(observer.PendingChangesAsync(Ct), item => item.Resolution?.RequestId == request.RequestId);
            Assert.Empty(change.Pending.Approvals);
            Assert.Empty(change.Pending.Questions);
            Assert.Equal(("answering-client", PendingResolutions.Allowed), (change.Resolution!.ResolvedBy, change.Resolution.Resolution));
        });
    }

    [Fact]
    public async Task KeyboardComposer_RebindsAfterReattach_AndSendsOrdinaryChatToHost()
    {
        await using var host = await HarnessHostFixture.CreateAsync("gk", TestContext.Current.CancellationToken);
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync();
        var observer = await host.ConnectAsync("keyboard-observer", Ct);
        await HeadlessGui.RunAsync(async () =>
        {
            await using var fleet = Fleet(host, gui);
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, null, Ct);
            var preferences = new GuiSettings(gui.App.Home);
            preferences.Save(preferences.Load() with { CloseAction = GuiSettings.CloseQuit });
            var window = new MainWindow(gui.App, chat);
            try
            {
                window.Show();
                var viewModel = window.ViewModel!;
                Assert.True(viewModel.IsDashboardPage);
                viewModel.ShowChatCommand.Execute(null);
                Dispatcher.UIThread.RunJobs();
                var view = Assert.Single(window.GetVisualDescendants().OfType<ChatView>());
                var parent = Assert.IsType<Grid>(view.Parent);
                var index = parent.Children.IndexOf(view);
                // Keep DataContext unchanged across unload/reload to exercise rebinding, not a context-change workaround.
                view.DataContext = viewModel;
                parent.Children.Remove(view);
                Dispatcher.UIThread.RunJobs();
                parent.Children.Insert(index, view);
                Dispatcher.UIThread.RunJobs();
                var input = view.FindControl<TextBox>("InputBox")!;
                Assert.True(input.IsEffectivelyVisible);
                Assert.True(input.Focus());
                window.KeyTextInput("typed through the ordinary composer");
                Assert.Equal("typed through the ordinary composer", viewModel.Composer.Input);
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                if (viewModel.SubmitCommand.ExecutionTask is { } submission)
                    await GuiTestEnvironment.PumpAsync(submission);
                await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                    () => viewModel.Messages.Any(message => message.Kind == MessageKind.User && message.Text == "typed through the ordinary composer"),
                    () => GuiTestEnvironment.DescribeChatState(viewModel));
                var snapshot = await observer.GetSessionSnapshotAsync(viewModel.CurrentSessionId.Value, Ct);
                Assert.Contains(snapshot.Events, item => item.Data is UserMessagePayload user
                    && user.Message.Content.OfType<Dsh.Llm.TextBlock>().Any(block => block.Text == "typed through the ordinary composer"));
                // Schedule close ahead of the queued sticky scroll without reentering message projection.
                viewModel.MessagesChanged += () =>
                {
                    if (viewModel.Messages.Any(message => message.Text == "last message before closing"))
                        Dispatcher.UIThread.Post(window.Close, DispatcherPriority.Send);
                };
                var agent = host.Environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Get(viewModel.CurrentSessionId)!;
                agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText("last message before closing")), new SurfaceOp.Append());
                await GuiTestEnvironment.WaitForAsync(viewModel.Messages,
                    () => viewModel.Messages.Any(message => message.Text == "last message before closing"));
                Dispatcher.UIThread.RunJobs();
                Assert.False(window.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static FleetWorkspace Fleet(HarnessHostFixture host, GuiTestEnvironment gui)
    {
        var options = FleetConnectionTests.TestOptions with { ClientName = "ordinary-gui-chat" };
        return new FleetWorkspace(new MachineStore(gui.App.Home), options,
            () => new FleetClient([MachineSpec.Local(host.Root)], options));
    }
}
