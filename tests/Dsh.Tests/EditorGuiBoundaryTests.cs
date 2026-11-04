using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Ide.Bridge;
using Dsh.RemoteHost;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class EditorGuiBoundaryTests
{
    [Fact]
    public async Task Picker_Captures_The_Actual_Chat_Turn_And_Does_Not_Change_With_Later_Events() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var chat = await environment.OpenChatAsync();
        await using var main = new MainViewModel(environment.App, chat);
        await main.PrepareEditorPickerCommand.ExecuteAsync(null);
        Assert.Null(main.EditorPicker!.CreateRequest().TurnId);
        environment.Agent.Session.Append(new TurnStartPayload(3));
        await chat.ReloadAsync(TestContext.Current.CancellationToken);
        await main.PrepareEditorPickerCommand.ExecuteAsync(null);
        var captured = main.EditorPicker!;
        Assert.Equal(environment.Agent.Id.Value, captured.CreateRequest().SessionId);
        Assert.Equal("3", captured.CreateRequest().TurnId);
        environment.Agent.Session.Append(new TurnStartPayload(4));
        await chat.ReloadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("3", captured.CreateRequest().TurnId);
        await main.PrepareEditorPickerCommand.ExecuteAsync(null);
        Assert.Equal("4", main.EditorPicker!.CreateRequest().TurnId);
    });

    [Fact]
    public async Task Files_Page_Passes_The_Host_Resolved_Root_To_The_Editor_Picker() => await HeadlessGui.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("er", ct);
        var options = FleetConnectionTests.TestOptions;
        var fleet = new FleetWorkspace(new FleetWorkspaceServices(new MachineStore(new HarnessHome(host.Root)), options, () => new FleetClient([MachineSpec.Local(host.Root)], options)));
        await using var workspace = new WorkspaceViewModel(fleet, (_, _, _) => { },
            pageContext: () => new(MachineSpec.LocalName, Path.Combine(host.Root, "nested", "..")));
        fleet.Start();
        var lease = await fleet.Find(MachineSpec.LocalName)!.WaitConnectedAsync(ct);
        await HarnessHostFixture.OpenProjectAsync(lease.Client, host.Root, ct);
        Directory.CreateDirectory(Path.Combine(host.Root, "nested"));
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var service = fixture.Service(fixture.Profile("resolved-root"));
        workspace.CreateEditorPicker = (machine, root) => new(service, _ => Task.FromResult<IEditorService>(service), root, machine);
        var icon = Geometry.Parse("M0,0 L1,1");
        workspace.RegisterHostPages(icon, icon, icon);
        workspace.OpenPage(workspace.Pages.Available.Single(page => page.Id == WorkspacePages.Files));
        var page = Assert.IsType<HostPageViewModel>(workspace.PageContent);
        await FleetConnectionTests.WaitForAsync(() => page.Content is FilesViewModel);
        var files = Assert.IsType<FilesViewModel>(page.Content);
        var root = Assert.Single(files.Roots).Path;
        Assert.Equal(host.Root, root);
        Assert.NotEqual(files.RootPath, root);
        Assert.Equal(root, files.OpenWith!.CreateRequest().WorkspaceRoot);
        await files.OpenWith.OpenAsync();
        Assert.Equal([root, root, "fixture"], File.ReadAllLines(fixture.Output));
    });

    [Theory]
    [InlineData(EditorFocus.Never, false)]
    [InlineData(EditorFocus.IfRunning, false)]
    [InlineData(EditorFocus.Always, true)]
    public async Task Gui_Editor_Panel_Only_Activates_And_Focuses_When_Explicitly_Requested(EditorFocus focus, bool activate) => await HeadlessGui.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("ef", ct);
        var options = FleetConnectionTests.TestOptions;
        await using var fleet = new FleetWorkspace(new FleetWorkspaceServices(new MachineStore(new HarnessHome(host.Root)), options, () => new FleetClient([MachineSpec.Local(host.Root)], options)));
        fleet.Start();
        await fleet.Find(MachineSpec.LocalName)!.WaitConnectedAsync(ct);
        TerminalViewModel? terminal = null;
        TerminalWindow? window = null;
        var input = new TextBox();
        var owner = new Window { Content = input };
        owner.Show();
        Assert.True(input.Focus());
        await using var adapter = new GuiEditorTerminalHost(fleet, vm =>
        {
            terminal = vm;
            window = new TerminalWindow(vm);
            window.Show(owner);
            return Task.CompletedTask;
        });
        try
        {
            var result = await adapter.LaunchAsync(new EditorLaunchCommand
            {
                Editor = "focus-shell-fixture", Executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                Arguments = OperatingSystem.IsWindows() ? ["/d", "/q"] : [], WorkingDirectory = host.Root,
                Kind = EditorKind.Terminal, Focus = focus,
            }, ct);
            Assert.True(result.Success, result.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(terminal!.Session);
            Assert.NotNull(terminal.PtyId);
            Assert.Equal(activate, terminal.ActivateOnOpen);
            Assert.Equal(activate, window!.ShowActivated);
            Assert.Equal(activate, window.FindControl<TerminalControl>("Terminal")!.IsFocused);
            if (!activate) Assert.True(input.IsFocused);
        }
        finally
        {
            window?.Close();
            if (terminal is not null) await terminal.DisposeAsync();
            owner.Close();
        }
    });

    [Fact]
    public async Task Shared_Root_Editors_Keep_Gui_Ptys_In_Their_Owning_View_Across_Close_And_Reopen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("el", ct);
        var command = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
        var arguments = OperatingSystem.IsWindows() ? """["/d", "/q"]""" : "[]";
        await using var gui = await GuiTestEnvironment.CreateIsolatedAsync($$"""
            logging:
              file: false
            plugins:
              "@deepseek-ai/dsh-ide":
                focus: never
                editors:
                  - id: lifecycle-shell
                    kind: terminal
                    command: "{{command}}"
                    actions:
                      open_workspace: {{arguments}}
            """);
        using var tui = new Dsh.Tui.ChatWindow(gui.App.Ctx, gui.Agent, gui.App.Home);
        tui.RegisterPaneTools();
        await HeadlessGui.RunAsync(() => ExerciseSharedGuiHostsAsync(host, gui, tui, ct));
    }

    private static async Task ExerciseSharedGuiHostsAsync(HarnessHostFixture host, GuiTestEnvironment gui, Dsh.Tui.ChatWindow tui, CancellationToken ct)
    {
        var views = new List<MainViewModel>();
        var panels = new List<(MainViewModel Owner, TerminalViewModel Terminal, TerminalWindow Window)>();
        try
        {
            var first = await OpenViewAsync();
            var second = await OpenViewAsync();
            await first.EditorPicker!.OpenAsync();
            var firstPanel = Assert.Single(panels);
            Assert.Same(first, firstPanel.Owner);
            Assert.NotNull(firstPanel.Terminal.Session);
            Assert.True(firstPanel.Window.IsVisible);
            Assert.False(firstPanel.Window.ShowActivated);
            await second.EditorPicker!.OpenAsync();
            Assert.Equal(2, panels.Count);
            var secondPanel = panels[1];
            Assert.Same(second, secondPanel.Owner);
            Assert.NotNull(secondPanel.Terminal.Session);
            Assert.NotEqual(firstPanel.Terminal.PtyId, secondPanel.Terminal.PtyId);
            Assert.Equal(1, tui.PaneCount);

            var firstPty = firstPanel.Terminal.PtyId!;
            await first.DisposeAsync();
            await FleetConnectionTests.WaitForAsync(() => host.Backend.Sessions.Get(firstPty)?.Status == HostSessionStatuses.Exited);
            Assert.False(secondPanel.Terminal.Completion.IsCompleted);
            var reopened = await OpenViewAsync();
            await reopened.EditorPicker!.OpenAsync();
            Assert.Equal(3, panels.Count);
            Assert.Same(reopened, panels[2].Owner);
            Assert.NotNull(panels[2].Terminal.Session);
            Assert.False(panels[2].Terminal.ActivateOnOpen);
            Assert.Equal(1, tui.PaneCount);

            var tuiLaunch = gui.App.Ctx.Get<IEditorService>(IEditorService.ServiceName)!.LaunchAsync(new()
            {
                WorkspaceRoot = gui.App.Home.Root, Path = gui.App.Home.Root, Editor = "lifecycle-shell",
                Action = EditorAction.OpenWorkspace, ExecutionTarget = EditorExecutionTarget.Local,
            }, ct).AsTask();
            await FleetConnectionTests.WaitForAsync(() => { tui.DrainUi(); return tuiLaunch.IsCompleted; });
            var result = await tuiLaunch;
            Assert.True(result.Success, result.Message);
            Assert.Equal("DSH-PTY", result.TerminalHost);
            Assert.Equal(2, tui.PaneCount);
            Assert.Equal(0, tui.FocusedPaneId);
            Assert.Equal(3, panels.Count);
        }
        finally
        {
            foreach (var view in views) await view.DisposeAsync();
            foreach (var panel in panels) panel.Window.Close();
        }

        async Task<MainViewModel> OpenViewAsync()
        {
            var options = FleetConnectionTests.TestOptions;
            var fleet = new FleetWorkspace(new FleetWorkspaceServices(new MachineStore(gui.App.Home), options, () => new FleetClient([MachineSpec.Local(host.Root)], options)));
            var chat = await HostChatSession.OpenAsync(fleet, host.Environment.Agent.Session.Header.Cwd!, host.Environment.Agent.Id.Value, ct);
            var view = new MainViewModel(gui.App, chat);
            views.Add(view);
            view.TerminalRequested += terminal =>
            {
                var window = new TerminalWindow(terminal);
                window.Closed += async (_, _) => await terminal.DisposeAsync();
                panels.Add((view, terminal, window));
                window.Show();
            };
            await view.PrepareEditorPickerCommand.ExecuteAsync(null);
            view.EditorPicker!.SelectedEditor = Assert.Single(view.EditorPicker.Editors, editor => editor.Id == "lifecycle-shell");
            return view;
        }
    }

    [Fact]
    public async Task Remote_Picker_Reads_The_Exact_Session_Binding_And_Redetects_Through_The_Host() => await HeadlessGui.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var executable = Path.Combine(fixture.Root, OperatingSystem.IsWindows() ? "remote-late.cmd" : "remote-late.sh");
        var service = fixture.Service(fixture.Profile("first"), fixture.Profile("second"), fixture.Profile("late") with { Command = executable });
        await using var host = await HarnessHostFixture.CreateAsync("eb", ct, core => new EditorHost(core, service));
        var client = await host.ConnectAsync("editor-gui-binding", ct);
        var editors = Assert.IsAssignableFrom<IRemoteEditors>(((IWorkspaceHost)client).GetCapability<IRemoteEditors>());
        await HarnessHostFixture.OpenProjectAsync(client, fixture.Root, ct);
        foreach (var (session, editor) in new[] { ("session-a", "second"), ("session-b", "first") })
        {
            var result = await editors.OpenAsync(new()
            {
                WorkspaceRoot = fixture.Root, Path = fixture.Root, Action = EditorAction.OpenWorkspace,
                Editor = editor, SessionId = session, RememberSelection = true, ExecutionTarget = EditorExecutionTarget.Local,
            }, ct);
            Assert.True(result.Success, result.Message);
        }
        var remote = new HostEditorService(editors);
        await remote.RefreshAsync(fixture.Root, ct, "session-a");
        Assert.Equal("second", remote.GetBinding(fixture.Root, "session-a")?.Editor.Value);
        await remote.RefreshAsync(fixture.Root, ct, "session-b");
        Assert.Equal("first", remote.GetBinding(fixture.Root, "session-b")?.Editor.Value);
        Assert.Equal("second", remote.GetBinding(fixture.Root, "session-a")?.Editor.Value);
        Assert.Null(remote.GetBinding(fixture.Root, "unbound-session"));
        var vm = new EditorOpenWithViewModel(null, async token =>
        {
            await remote.RefreshAsync(fixture.Root, token, "session-a");
            return remote;
        }, fixture.Root, "fixture-remote", "session-a");
        await vm.RefreshAsync();
        Assert.Equal("second", vm.SelectedEditor?.Id);
        Assert.Equal(["first", "second"], vm.Editors.Select(editor => editor.Id));
        File.Copy(fixture.Executable, executable);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Assert.DoesNotContain(await remote.Catalog.DetectAsync(), installation => installation.Editor.Value == "late");
        await vm.RefreshAsync();
        vm.SelectedEditor = Assert.Single(vm.Editors, editor => editor.Id == "late");
        File.Delete(fixture.Output);
        await vm.OpenAsync();
        Assert.Equal([fixture.Root, fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
        var reopened = new HostEditorService(editors);
        await reopened.RefreshAsync(fixture.Root, ct, "session-a");
        Assert.Equal("late", reopened.GetBinding(fixture.Root, "session-a")?.Editor.Value);
        await reopened.RefreshAsync(fixture.Root, ct, "session-b");
        Assert.Equal("first", reopened.GetBinding(fixture.Root, "session-b")?.Editor.Value);
    });

    private sealed class EditorHost(IRemoteHostBackend core, IEditorService service) : IRemoteHostBackend, IRemoteEditorBackend
    {
        public HostPendingDecisions Pending => core.Pending;
        public HostSessionRegistry Sessions => core.Sessions;
        public IRemoteSessionBackend Session => core.Session;
        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken) => core.InfoAsync(cancellationToken);
        public IRemoteAgentBackend? Agent => core.Agent;
        public IRemotePtyBackend? Pty => core.Pty;
        public HostFileSystem? Files => core.Files;
        public HostGitService? Git => core.Git;
        public HostNotificationCenter? Notifications => core.Notifications;
        public IRemoteSessionEditingBackend? SessionEditing => core.SessionEditing;
        public IRemotePluginsBackend? Plugins => core.Plugins;
        public IRemoteNetworkBackend? Network => core.Network;
        public IRemoteEditorBackend Editor => this;
        public IEditorService Service => service;
        public EditorClientDispatcher? Clients => null;
        public HostPathPolicy? Paths => core.Files?.Policy;
    }
}
