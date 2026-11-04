using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Fleet;
using Dsh.Gui.Services;
using Dsh.Gui.Terminal;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;
using Dsh.Ide;
using Dsh.Ide.Catalog;
using Dsh.Ide.History;
using Dsh.Ide.Launch;
using Dsh.RemoteHost;
using Dsh.Runtime.Composition;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class EditorGuiTests
{
    [Fact]
    public async Task Ide_Section_Is_Navigable_And_The_Visible_Save_Button_Persists_The_Draft() => await HeadlessGui.RunAsync(async () =>
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var window = new MainWindow(environment.App, await environment.OpenChatAsync());
        window.Show();
        try
        {
            var main = window.ViewModel!;
            main.ShowSettingsCommand.Execute(null);
            var section = main.Preferences.Sections.Single(item => item.Section == SettingsSection.Ide);
            section.SelectCommand!.Execute(section);
            Dispatcher.UIThread.RunJobs();
            Assert.True(main.Preferences.IsIdeSection);
            var view = window.GetVisualDescendants().OfType<EditorSettingsView>().Single();
            Assert.True(view.IsEffectivelyVisible);
            main.Preferences.Ide.DefaultEditor = "vscode";
            main.Preferences.Ide.AddTriggerCommand.Execute(null);
            main.Preferences.Ide.SelectedTrigger!.Event = "session.resume";
            main.Preferences.Ide.SelectedTrigger.Action = "open_workspace";
            main.Preferences.Ide.SelectedTrigger.Once = "workspace";
            Assert.True(view.FindControl<Button>("SaveEditorSettings")!.Focus());
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            var saved = new EditorSettingsViewModel(new SettingsFacade(environment.App.Ctx, environment.App.Home));
            Assert.Equal("vscode", saved.DefaultEditor);
            Assert.Equal("session.resume", Assert.Single(saved.Triggers).Event);
            Assert.Equal("workspace", saved.Triggers[0].Once);
        }
        finally
        {
            window.Close();
            await window.ViewModel!.DisposeAsync();
            Dispatcher.UIThread.RunJobs();
        }
    });

    [Fact]
    public async Task Settings_Save_Ordered_Profiles_Triggers_Options_And_Unknown_Keys()
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var home = environment.App.Home;
        File.WriteAllText(home.SettingsFile, """
            # IDE GUI preservation fixture
            plugins:
              "@deepseek-ai/dsh-ide":
                permissions:
                  write: false
                custom_option: keep
                editors: [vscode, rider]
                triggers:
                  - on: session.start
                    action: open_workspace
                    custom_rule: keep-rule
                  - on: manual
                    action: open_file
                history:
                  providers: [vscode]
                  custom_history: keep-history
            """);
        var facade = new SettingsFacade(environment.App.Ctx, home);
        var vm = new EditorSettingsViewModel(facade);
        Assert.Equal(["vscode", "rider"], vm.Editors.Select(row => row.Label));
        Assert.Equal("vscode", vm.HistoryProviders);
        Assert.All(vm.Editors, row => Assert.True(row.Read().ContainsKey("remote_executables")));
        vm.SelectedEditor = vm.Editors[1];
        vm.MoveEditorUpCommand.Execute(null);
        vm.Editors[0].Enabled = false;
        vm.SelectedTrigger = vm.Triggers[1];
        vm.MoveTriggerUpCommand.Execute(null);
        vm.SelectedTrigger.Yaml = """
            on: tool.after_write
            action: open_file
            editors: [rider, vscode]
            mode: all
            once: turn
            cooldown: 45s
            focus: if_running
            stop_on_success: false
            enabled: true
            when:
              extensions: [.cs]
              workspace: [.idea]
              globs: ["src/**"]
              project_types: [dotnet]
              machine_types: [local]
              host_editors: [rider]
              session_sources: [gui]
            """;
        vm.DefaultEditor = "rider";
        vm.StrictMatch = true;
        vm.AllowOutsideWorkspace = true;
        vm.Focus = "always";
        vm.ReuseWindow = "never";
        vm.Fallback = "os-default";
        vm.HistoryProviders = "jetbrains, vscode";
        vm.HistoryTimeline = true;
        vm.HistoryLimit = 23;
        vm.SaveCommand.Execute(null);
        var reloaded = new EditorSettingsViewModel(facade);
        Assert.True(new[] { "rider", "vscode" }.SequenceEqual(reloaded.Editors.Select(row => row.Label)), vm.Status);
        Assert.False(reloaded.Editors[0].Enabled);
        Assert.Equal(["tool.after_write", "session.start"], reloaded.Triggers.Select(row => row.Label));
        Assert.Equal("keep-rule", reloaded.Triggers[1].Read()["custom_rule"]);
        Assert.Equal("rider", reloaded.DefaultEditor);
        Assert.True(reloaded.StrictMatch);
        Assert.True(reloaded.AllowOutsideWorkspace);
        Assert.Equal("always", reloaded.Focus);
        Assert.Equal("never", reloaded.ReuseWindow);
        Assert.Equal("os-default", reloaded.Fallback);
        Assert.Equal("jetbrains, vscode", reloaded.HistoryProviders);
        Assert.True(reloaded.HistoryTimeline);
        Assert.Equal(23, reloaded.HistoryLimit);
        var settings = HarnessSettings.Load(home);
        var canonical = settings.Plugins[EditorSettingsViewModel.Package];
        Assert.Equal("keep", canonical.Parameters["custom_option"]);
        Assert.False(canonical.Permissions!.Write);
        Assert.Contains("# IDE GUI preservation fixture", File.ReadAllText(home.SettingsFile));
        var history = Assert.IsType<Dictionary<string, object?>>(canonical.Parameters["history"]);
        Assert.Equal("keep-history", history["custom_history"]);
        var options = EditorOptions.From(canonical.Parameters);
        Assert.Single(options.Editors);
        Assert.Equal("vscode", options.Editors[0].Id.Value);
        Assert.Equal("tool.after_write", options.Triggers[0].On);
        Assert.Equal(TimeSpan.FromSeconds(45), options.Triggers[0].Cooldown);
    }

    [Theory]
    [InlineData("|-", "\n", "keep")]
    [InlineData("|-", "\r\n", "keep")]
    [InlineData("|", "\n", "keep\n")]
    [InlineData(">-", "\n", "keep")]
    public async Task Plugin_Save_Preserves_An_Unknown_Block_Scalar_And_The_Following_Top_Level_Setting(
        string style, string newline, string expected)
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var home = environment.App.Home;
        File.WriteAllText(home.SettingsFile, string.Join(newline,
            "plugins:",
            "  \"@deepseek-ai/dsh-ide\":",
            $"    extra: {style}",
            "      keep",
            "# Custom metadata belongs to the user, not the IDE plugin.",
            "custom: keep",
            ""));
        var facade = new SettingsFacade(environment.App.Ctx, home);
        Assert.Equal(expected, facade.Load().Plugins[EditorSettingsViewModel.Package].Parameters["extra"]);

        facade.SavePluginParameters(EditorSettingsViewModel.Package, new Dictionary<string, object?> { ["strict_match"] = true });

        var saved = HarnessSettings.Load(home).Plugins[EditorSettingsViewModel.Package];
        Assert.True(saved.Enabled);
        Assert.True(Assert.IsType<bool>(saved.Parameters["strict_match"]));
        Assert.Equal(expected, saved.Parameters["extra"]);
        var document = File.ReadAllText(home.SettingsFile);
        Assert.Equal("keep", YamlConfig.LoadMapping(document)["custom"]);
        Assert.Contains("# Custom metadata belongs to the user, not the IDE plugin.", document);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task Plugin_Save_Inserts_Before_The_Immediate_Sibling_After_A_Block_Scalar(string newline)
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var home = environment.App.Home;
        File.WriteAllText(home.SettingsFile, string.Join(newline,
            "plugins:",
            "  \"@deepseek-ai/dsh-ide\":",
            "    extra: |-",
            "      keep",
            "custom: keep",
            ""));
        var facade = new SettingsFacade(environment.App.Ctx, home);
        Assert.Equal("keep", facade.Load().Plugins[EditorSettingsViewModel.Package].Parameters["extra"]);

        facade.SavePluginParameters(EditorSettingsViewModel.Package, new Dictionary<string, object?> { ["strict_match"] = true });

        var saved = HarnessSettings.Load(home).Plugins[EditorSettingsViewModel.Package];
        Assert.True(saved.Enabled);
        Assert.True(Assert.IsType<bool>(saved.Parameters["strict_match"]));
        Assert.Equal("keep", saved.Parameters["extra"]);
        Assert.Equal("keep", YamlConfig.LoadMapping(File.ReadAllText(home.SettingsFile))["custom"]);
    }

    [Theory]
    [InlineData("keep\n")]
    [InlineData("first\nsecond\n")]
    [InlineData("first\r\nsecond\rthird\tend")]
    [InlineData("quoted \"value\" and \\path")]
    [InlineData("\0\a\b\v\f\u001b\u001f\u007f\u009f")]
    [InlineData("next\u0085line\u2028separator\u2029end")]
    public async Task Plugin_Save_Roundtrips_Multiline_And_Control_Text_In_Values_And_Unknown_Keys(string value)
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var home = environment.App.Home;
        File.WriteAllText(home.SettingsFile, """
            plugins:
              "@deepseek-ai/dsh-ide":
                extra: keep
            # User metadata must survive IDE option changes.
            custom: keep
            """);
        var facade = new SettingsFacade(environment.App.Ctx, home);
        var parameters = new Dictionary<string, object?>
        {
            ["user_note"] = value,
            ["custom_metadata\n"] = value,
        };

        facade.SavePluginParameters(EditorSettingsViewModel.Package, parameters);

        var initial = HarnessSettings.Load(home).Plugins[EditorSettingsViewModel.Package];
        Assert.Equal(value, initial.Parameters["user_note"]);
        Assert.Equal(value, initial.Parameters["custom_metadata\n"]);

        facade.SavePluginParameters(EditorSettingsViewModel.Package, new Dictionary<string, object?> { ["strict_match"] = true });

        var saved = HarnessSettings.Load(home).Plugins[EditorSettingsViewModel.Package];
        Assert.True(Assert.IsType<bool>(saved.Parameters["strict_match"]));
        Assert.Equal(value, saved.Parameters["user_note"]);
        Assert.Equal(value, saved.Parameters["custom_metadata\n"]);
        Assert.Equal("keep", saved.Parameters["extra"]);
        var document = File.ReadAllText(home.SettingsFile);
        Assert.Equal("keep", YamlConfig.LoadMapping(document)["custom"]);
        Assert.Contains("# User metadata must survive IDE option changes.", document);
    }

    [Theory]
    [InlineData("{extra: keep}", false, false)]
    [InlineData("{extra: [keep]}", true, false)]
    [InlineData("{extra: [keep],}", true, false)]
    [InlineData("{extra: [keep]}", true, true)]
    public async Task Plugin_Save_Preserves_Flow_Values_And_The_Following_Setting(
        string plugin, bool sequence, bool flowRoot)
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var home = environment.App.Home;
        File.WriteAllText(home.SettingsFile, "# Preserve the custom integration setting.\n" + (flowRoot
            ? $"{{plugins: {{\"@deepseek-ai/dsh-ide\": {plugin}}}, custom: keep}}\n"
            : $"plugins:\n  \"@deepseek-ai/dsh-ide\": {plugin}\ncustom: keep\n"));
        var facade = new SettingsFacade(environment.App.Ctx, home);

        facade.SavePluginParameters(EditorSettingsViewModel.Package, new Dictionary<string, object?> { ["strict_match"] = true });

        var saved = HarnessSettings.Load(home).Plugins[EditorSettingsViewModel.Package];
        Assert.True(saved.Enabled);
        Assert.True(Assert.IsType<bool>(saved.Parameters["strict_match"]));
        if (sequence)
            Assert.Equal("keep", Assert.Single(Assert.IsAssignableFrom<IEnumerable<object?>>(saved.Parameters["extra"])));
        else
            Assert.Equal("keep", saved.Parameters["extra"]);
        var document = File.ReadAllText(home.SettingsFile);
        Assert.Equal("keep", YamlConfig.LoadMapping(document)["custom"]);
        Assert.Contains("# Preserve the custom integration setting.", document);
    }

    [Fact]
    public async Task Saving_Unrelated_Editor_Settings_Does_Not_Reenable_Explicitly_Disabled_History_Providers()
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var home = environment.App.Home;
        File.WriteAllText(home.SettingsFile, """
            plugins:
              "@deepseek-ai/dsh-ide":
                history:
                  providers: null
                  timeline: true
            """);
        var facade = new SettingsFacade(environment.App.Ctx, home);
        Assert.Empty(IdeHistoryOptions.From(facade.Load().Plugins[EditorSettingsViewModel.Package].Parameters).Providers);
        var vm = new EditorSettingsViewModel(facade);
        vm.StrictMatch = true;
        vm.SaveCommand.Execute(null);
        var saved = HarnessSettings.Load(home).Plugins[EditorSettingsViewModel.Package];
        Assert.True(Assert.IsType<bool>(saved.Parameters["strict_match"]));
        Assert.Empty(IdeHistoryOptions.From(saved.Parameters).Providers);
    }

    [Fact]
    public async Task Files_Selection_Opens_Real_Host_File_Or_Workspace_Through_The_Picker()
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var root = environment.App.Home.Root;
        File.WriteAllText(Path.Combine(root, "editor-selection.cs"), "fixture");
        var chat = await environment.OpenChatAsync();
        var client = await chat.ClientAsync(TestContext.Current.CancellationToken);
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var service = fixture.Service(fixture.Profile("file-fixture"));
        var picker = new EditorOpenWithViewModel(service, _ => Task.FromResult<IEditorService>(service), root, "fixture-remote");
        var files = new FilesViewModel(client.Files, root, OperatingSystem.IsWindows() ? RemotePathStyle.Windows : RemotePathStyle.Posix)
        {
            OpenWith = picker,
        };
        Assert.True(await files.LoadAsync());
        await picker.RefreshAsync();
        var directory = Assert.Single(files.Roots);
        Assert.Equal(directory.Path, picker.Path);
        Assert.True(picker.IsDirectory);
        await picker.OpenAsync();
        Assert.Equal(root, File.ReadAllLines(fixture.Output)[0]);
        files.Selected = directory.Children.Single(node => node.Name == "editor-selection.cs");
        Assert.False(picker.IsDirectory);
        Assert.Equal(files.Selected.Path, picker.Path);
        await picker.OpenAsync();
        Assert.Equal(files.Selected.Path, File.ReadAllLines(fixture.Output)[0]);
        Assert.Equal("file-fixture", service.GetBinding(root)?.Editor.Value);
    }

    [Fact]
    public async Task Picker_Uses_Actual_Local_And_Remote_Catalogues_And_Remembers_Selection() => await HeadlessGui.RunAsync(async () =>
    {
        var root = TempTree.CreateDirectory("editor-gui-picker");
        try
        {
            var localRoot = Directory.CreateDirectory(Path.Combine(root, "local")).FullName;
            var remoteRoot = Directory.CreateDirectory(Path.Combine(root, "remote")).FullName;
            using var localFixture = new EditorLauncherTests.FakeEditorFixture();
            using var remoteFixture = new EditorLauncherTests.FakeEditorFixture();
            var local = localFixture.Service(localFixture.Profile("local-one"), localFixture.Profile("local-two"));
            var remote = remoteFixture.Service(remoteFixture.Profile("remote-one"), remoteFixture.Profile("remote-two"));
            var vm = new EditorOpenWithViewModel(local, _ => Task.FromResult<IEditorService>(remote), remoteRoot, "fixture-remote", "session");
            await vm.RefreshAsync();
            Assert.Equal(["remote-one", "remote-two"], vm.Editors.Select(editor => editor.Id));
            vm.SelectedEditor = vm.Editors[1];
            var view = new EditorOpenWithView { DataContext = vm };
            var window = new Window { Content = view, Width = 500, Height = 600 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var button = view.FindControl<Button>("OpenSelectedEditor")!;
                Assert.True(button.Focus());
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await vm.OpenCommand.ExecutionTask!;
                Assert.False(File.Exists(localFixture.Output));
                Assert.Equal("remote-two", remote.GetBinding(remoteRoot, "session")?.Editor.Value);
                var reopened = new EditorOpenWithViewModel(local, _ => Task.FromResult<IEditorService>(remote), remoteRoot, "fixture-remote", "session");
                await reopened.RefreshAsync();
                Assert.Equal("remote-two", reopened.SelectedEditor?.Id);
                vm.LocalWorkspace = localRoot;
                vm.Destination = "local";
                await vm.RefreshAsync();
                vm.Path = remoteRoot.TrimEnd(Path.DirectorySeparatorChar) + "/source.cs";
                vm.IsDirectory = false;
                vm.SelectedEditor = vm.Editors.Single(editor => editor.Id == "local-two");
                await vm.OpenAsync();
                Assert.Equal("local-two", local.GetBinding(localRoot, "session")?.Editor.Value);
                var recorded = File.ReadAllLines(localFixture.Output);
                Assert.Equal(Path.Combine(localRoot, "source.cs"), recorded[0]);
                Assert.Equal(localRoot, recorded[^2]);
                Assert.Equal(remoteRoot, File.ReadAllLines(remoteFixture.Output)[0]);
            }
            finally { window.Close(); }
        }
        finally { TempTree.Delete(root); }
    });

    [Fact]
    public void Picker_Requires_Explicit_Local_Mapping_For_Remote_Files()
    {
        var vm = new EditorOpenWithViewModel(null, _ => throw new InvalidOperationException(), "/remote/workspace", "fixture-remote")
        { Destination = "local", Path = "/remote/workspace/file.cs", IsDirectory = false };
        Assert.Throws<ArgumentException>(() => vm.CreateRequest());
        var localRoot = TempTree.CreateDirectory("editor-gui-map");
        try
        {
            vm.LocalWorkspace = localRoot;
            var request = vm.CreateRequest();
            Assert.Equal(Path.Combine(localRoot, "file.cs"), request.Path);
            Assert.Equal(localRoot, request.WorkspaceRoot);
            vm.Path = "/remote/other/file.cs";
            Assert.Throws<ArgumentException>(() => vm.CreateRequest());
        }
        finally { TempTree.Delete(localRoot); }
    }

    [Fact]
    public async Task Picker_Freezes_Destination_And_Request_Before_Waiting_For_The_Host() => await HeadlessGui.RunAsync(async () =>
    {
        using var localFixture = new EditorLauncherTests.FakeEditorFixture();
        using var remoteFixture = new EditorLauncherTests.FakeEditorFixture();
        var local = localFixture.Service(localFixture.Profile("local"));
        var remote = remoteFixture.Service(remoteFixture.Profile("remote"));
        var pendingHost = new TaskCompletionSource<IEditorService>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new EditorOpenWithViewModel(local, _ => pendingHost.Task, remoteFixture.Root, "fixture-remote")
        {
            SelectedEditor = new("remote", "Remote editor", remoteFixture.Executable),
        };
        var opening = vm.OpenAsync();
        vm.LocalWorkspace = localFixture.Root;
        vm.Destination = "local";
        vm.Path = Path.Combine(remoteFixture.Root, "different.cs");
        vm.IsDirectory = false;
        pendingHost.SetResult(remote);
        await opening;
        Assert.Equal(remoteFixture.Root, File.ReadAllLines(remoteFixture.Output)[0]);
        Assert.False(File.Exists(localFixture.Output));
    });

    [Theory]
    [InlineData(null, "vscode, jetbrains, zed")]
    [InlineData("zed", "zed")]
    [InlineData("[jetbrains, zed]", "jetbrains, zed")]
    public async Task Saving_Unrelated_Ide_Options_Preserves_Default_Scalar_And_List_History_Providers(string? providers, string expected)
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var configuration = providers is null ? $"plugins:\n  \"{EditorSettingsViewModel.Package}\": true\n"
            : $"plugins:\n  \"{EditorSettingsViewModel.Package}\":\n    history:\n      providers: {providers}\n";
        File.WriteAllText(environment.App.Home.SettingsFile, configuration);
        var vm = new EditorSettingsViewModel(new SettingsFacade(environment.App.Ctx, environment.App.Home));
        Assert.Equal(expected, vm.HistoryProviders);
        vm.StrictMatch = true;
        vm.SaveCommand.Execute(null);
        var saved = HarnessSettings.Load(environment.App.Home).Plugins[EditorSettingsViewModel.Package];
        Assert.Equal(expected.Split(", "), EditorOptions.From(saved.Parameters).History.Providers);
    }

    [Fact]
    public async Task Redetect_Finds_A_Newly_Installed_Command_Without_Changing_The_Profile() => await HeadlessGui.RunAsync(async () =>
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var executable = Path.Combine(fixture.Root, OperatingSystem.IsWindows() ? "late-editor.cmd" : "late-editor.sh");
        var profile = fixture.Profile("late") with { Command = executable };
        var service = fixture.Service(profile);
        var vm = new EditorOpenWithViewModel(service, _ => Task.FromResult<IEditorService>(service), fixture.Root, MachineSpec.LocalName);
        await vm.RefreshAsync();
        Assert.Empty(vm.Editors);
        File.Copy(fixture.Executable, executable);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await vm.RefreshAsync();
        Assert.Equal("late", Assert.Single(vm.Editors).Id);
        await vm.OpenAsync();
        Assert.Equal([fixture.Root, fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
    });

    [Fact]
    public async Task Gui_Pty_Uses_Real_Panel_Input_Environment_Exit_And_Close_Ownership() => await HeadlessGui.RunAsync(async () =>
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await HarnessHostFixture.CreateAsync("eg", ct);
        var options = FleetConnectionTests.TestOptions;
        await using var fleet = new FleetWorkspace(new FleetWorkspaceServices(new MachineStore(new HarnessHome(host.Root)), options, () => new FleetClient([MachineSpec.Local(host.Root)], options)));
        fleet.Start();
        await fleet.Find(MachineSpec.LocalName)!.WaitConnectedAsync(ct);
        TerminalViewModel? terminal = null;
        TerminalWindow? window = null;
        await using var adapter = new GuiEditorTerminalHost(fleet, vm =>
        {
            terminal = vm;
            window = new TerminalWindow(vm);
            window.Closed += async (_, _) => await vm.DisposeAsync();
            window.Show();
            return Task.CompletedTask;
        });
        var command = new EditorLaunchCommand
        {
            Editor = "gui-shell-fixture", Executable = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            Arguments = OperatingSystem.IsWindows() ? ["/d", "/q"] : [], WorkingDirectory = host.Root,
            Kind = EditorKind.Terminal, Environment = new Dictionary<string, string> { ["DSH_EDITOR_TEST"] = "editor-env-42" },
        };
        try
        {
            var result = await adapter.LaunchAsync(command, ct);
            Assert.True(result.Success, result.Message);
            Assert.Equal("dsh-gui-pty", result.TerminalHost);
            Assert.NotNull(terminal!.Session);
            Assert.NotNull(terminal.PtyId);
            var activeWindow = Assert.IsType<TerminalWindow>(window);
            var control = activeWindow.FindControl<TerminalControl>("Terminal")!;
            Assert.True(control.Focus());
            activeWindow.KeyTextInput(OperatingSystem.IsWindows() ? "echo %DSH_EDITOR_TEST%" : "echo $DSH_EDITOR_TEST");
            activeWindow.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await FleetConnectionTests.WaitForAsync(() => HasLine(terminal.Session!, "editor-env-42"));
            activeWindow.KeyTextInput("exit 7");
            activeWindow.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(7, await terminal.Completion.WaitAsync(TimeSpan.FromSeconds(10), ct));
            Assert.Equal(7, host.Backend.Sessions.Get(terminal.PtyId!)?.ExitCode);
            activeWindow.Close();
            await terminal.DisposeAsync();
            result = await adapter.LaunchAsync(command, ct);
            Assert.True(result.Success, result.Message);
            var owned = terminal.PtyId!;
            window!.Close();
            await terminal.DisposeAsync();
            await FleetConnectionTests.WaitForAsync(() => host.Backend.Sessions.Get(owned)?.Status == HostSessionStatuses.Exited);
        }
        finally
        {
            window?.Close();
            if (terminal is not null) await terminal.DisposeAsync();
        }
    });

    private static bool HasLine(TerminalSession session, string expected)
    {
        lock (session.Gate)
        {
            var screen = session.Screen;
            for (var line = -screen.ScrollbackCount; line < screen.Height; line++)
            {
                var text = new System.Text.StringBuilder();
                foreach (var cell in screen.LineAt(line)) if (cell.Character != '\0') text.Append(cell.Character);
                if (text.ToString().Trim() == expected) return true;
            }
            return false;
        }
    }

}
