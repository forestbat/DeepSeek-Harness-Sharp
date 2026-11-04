using Dsh.Core;
using Dsh.Ide;
using Dsh.Ide.Catalog;
using Dsh.Ide.Bridge;
using Dsh.Ide.Launch;
using Dsh.Ide.Selection;

namespace Dsh.Tests;

// FakeEditorFixture records argv/cwd/env only; these tests do not claim real IDE compatibility.
public sealed class EditorLauncherTests
{
    [Fact]
    public async Task FakeEditor_Records_Literal_Arguments_Cwd_And_Global_Environment()
    {
        using var fixture = new FakeEditorFixture();
        var target = Path.Combine(fixture.Root, "source & !percent% 'quoted'.cs");
        File.WriteAllText(target, "fixture");
        var descriptor = fixture.Profile("fixture") with { WorkingDirectory = "{dir}", Environment = new Dictionary<string, string> { ["DSH_FAKE_EDITOR_OUTPUT"] = fixture.Output, ["DSH_FAKE_EDITOR_VALUE"] = "literal & ! % {file}" } };
        var service = fixture.Service(descriptor);
        var result = await service.LaunchAsync(new() { WorkspaceRoot = fixture.Root, Path = target, Action = EditorAction.OpenAtPosition, Line = 42, Column = 7, Editor = "fixture", RememberSelection = true, SessionId = "session" }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.ProcessId);
        var recorded = File.ReadAllLines(fixture.Output);
        Assert.Equal([target, "42", "7", fixture.Root, $"literal & ! % {Path.GetFileName(target)}"], recorded);
        Assert.Equal(new EditorId("fixture"), service.GetBinding(fixture.Root, "session")?.Editor);
    }

    [Fact]
    public async Task Order_Uses_Explicit_Workspace_Binding_Trigger_Then_Global()
    {
        using var fixture = new FakeEditorFixture();
        var first = fixture.Profile("first");
        var second = fixture.Profile("second");
        var service = fixture.Service(first, second);
        var request = new EditorRequest { WorkspaceRoot = fixture.Root, Action = EditorAction.OpenFile, Path = "test.cs", DryRun = true };
        Assert.Equal("first", (await service.LaunchAsync(request, TestContext.Current.CancellationToken)).Editor?.Value);
        service.Bind(new(fixture.Root, "second"));
        Assert.Equal("second", (await service.LaunchAsync(request, TestContext.Current.CancellationToken)).Editor?.Value);
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".dsh"));
        File.WriteAllText(Path.Combine(fixture.Root, ".dsh", "ide.yaml"), "default_editor: first\n");
        Assert.Equal("first", (await service.LaunchAsync(request with { TriggerEditors = ["second"] }, TestContext.Current.CancellationToken)).Editor?.Value);
        Assert.Equal("second", (await service.LaunchAsync(request with { Editor = "second" }, TestContext.Current.CancellationToken)).Editor?.Value);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public async Task Matching_Strict_Fallback_And_Action_Capabilities_Are_Explained()
    {
        using var fixture = new FakeEditorFixture();
        var a = fixture.Profile("a") with { When = new() { Extensions = [".java"] } };
        var b = fixture.Profile("b") with { When = new() { Extensions = [".cs"] } };
        var request = new EditorRequest { WorkspaceRoot = fixture.Root, Path = "test.cs", Action = EditorAction.OpenFile, DryRun = true };
        var service = fixture.Service(a, b);
        var result = await service.LaunchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("b", result.Editor?.Value);
        Assert.Contains(new EditorId("a"), result.Alternatives);
        Assert.False(result.Decision.Steps.Single(s => s.Editor.Value == "a").Matched);
        var strict = fixture.Service(new EditorOptions { Editors = [a, b], StrictMatch = true }, a, b);
        var missing = await strict.LaunchAsync(request with { Path = "test.unknown" }, TestContext.Current.CancellationToken);
        Assert.Equal(EditorErrorCode.NOT_FOUND, missing.Error);
        Assert.All(missing.Decision.Steps, step => Assert.False(step.Matched));
        File.WriteAllText(Path.Combine(fixture.Root, "left.cs"), "left");
        File.WriteAllText(Path.Combine(fixture.Root, "right.cs"), "right");
        var unsupported = await service.LaunchAsync(request with { Editor = "a", Action = EditorAction.OpenDiff, Left = "left.cs", Right = "right.cs" }, TestContext.Current.CancellationToken);
        Assert.Equal(EditorErrorCode.UNSUPPORTED, unsupported.Error);
        Assert.Equal("a", (await service.LaunchAsync(request with { Editor = "a" }, TestContext.Current.CancellationToken)).Editor?.Value);
    }

    [Fact]
    public async Task Failed_Process_Falls_Through_But_Explicit_Editor_Does_Not()
    {
        using var fixture = new FakeEditorFixture();
        var failing = fixture.Profile("fail", fail: true);
        var working = fixture.Profile("working");
        var service = fixture.Service(failing, working);
        var request = new EditorRequest { WorkspaceRoot = fixture.Root, Path = "test.cs", Action = EditorAction.OpenAtPosition };
        var result = await service.LaunchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("working", result.Editor?.Value);
        Assert.Equal(EditorErrorCode.LAUNCH_FAILED, result.Decision.Steps.Single(s => s.Editor.Value == "fail").Error);
        var explicitResult = await service.LaunchAsync(request with { Editor = "fail" }, TestContext.Current.CancellationToken);
        Assert.False(explicitResult.Success);
        Assert.Single(explicitResult.Decision.CandidateOrder);
    }

    [Fact]
    public async Task Workspace_Cannot_Define_Executable_Or_Escape_Through_Symlink()
    {
        using var fixture = new FakeEditorFixture();
        var service = fixture.Service(fixture.Profile("fixture"));
        var request = new EditorRequest { WorkspaceRoot = fixture.Root, Path = "file.cs", Action = EditorAction.OpenFile, DryRun = true };
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".dsh"));
        File.WriteAllText(Path.Combine(fixture.Root, ".dsh", "ide.yaml"), "default_editor: fixture\nenv:\n  EVIL: value\n");
        Assert.Equal(EditorErrorCode.INVALID_CONFIGURATION, (await service.LaunchAsync(request, TestContext.Current.CancellationToken)).Error);
        File.Delete(Path.Combine(fixture.Root, ".dsh", "ide.yaml"));
        Assert.Equal(EditorErrorCode.OUTSIDE_WORKSPACE, (await service.LaunchAsync(request with { Path = "../outside.cs" }, TestContext.Current.CancellationToken)).Error);
        if (!OperatingSystem.IsWindows())
        {
            var other = Directory.CreateDirectory(Path.Combine(fixture.Root, "..", $"outside-{Guid.NewGuid():N}"));
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "linked"), other.FullName);
                Assert.Equal(EditorErrorCode.OUTSIDE_WORKSPACE, (await service.LaunchAsync(request with { Path = "linked/missing.cs" }, TestContext.Current.CancellationToken)).Error);
            }
            finally { other.Delete(); }
        }
    }

    [Fact]
    public async Task Remote_Executables_Are_Detected_Before_Local_Candidates()
    {
        using var fixture = new FakeEditorFixture();
        var descriptor = fixture.Profile("remote") with
        {
            Command = "missing-editor-for-remote-executable-test",
            RemoteExecutableCandidates = [fixture.Executable],
        };
        var service = fixture.Service(descriptor);
        var result = await service.LaunchAsync(new()
        {
            WorkspaceRoot = fixture.Root,
            Path = "test.cs",
            Action = EditorAction.OpenFile,
            Editor = "remote",
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Equal("remote", result.Editor?.Value);
    }

    [Fact]
    public void Remote_Executables_Are_Read_From_Editor_Configuration()
    {
        using var fixture = new FakeEditorFixture();
        var options = EditorOptions.From(new Dictionary<string, object?>
        {
            ["editors"] = new object?[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "remote",
                    ["command"] = "remote-editor",
                    ["remote_executables"] = new[] { fixture.Executable },
                    ["actions"] = new Dictionary<string, object?> { ["open_file"] = new[] { "{path}" } },
                },
            },
        });
        Assert.Equal([fixture.Executable], options.Editors.Single().RemoteExecutableCandidates);
    }

    [Fact]
    public async Task Late_Registration_Invalidates_Detection_And_Disposal_Restores_Catalog()
    {
        using var fixture = new FakeEditorFixture();
        var catalog = new EditorCatalog([]);
        Assert.Empty(await catalog.DetectAsync(cancellationToken: TestContext.Current.CancellationToken));
        var descriptor = fixture.Profile("late");
        using (catalog.Register(descriptor))
        {
            Assert.Single(await catalog.DetectAsync(cancellationToken: TestContext.Current.CancellationToken));
            var snapshot = catalog.Descriptors[0];
            snapshot.Actions[EditorAction.OpenFile][0] = "mutated";
            Assert.NotEqual("mutated", catalog.Find("late")!.Actions[EditorAction.OpenFile][0]);
            Assert.True((await new EditorLauncher(catalog, new() { Editors = [descriptor] }).LaunchAsync(new() { WorkspaceRoot = fixture.Root, Editor = "late", Action = EditorAction.OpenFile, DryRun = true }, TestContext.Current.CancellationToken)).Success);
        }
        Assert.Empty(await catalog.DetectAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(catalog.Descriptors);
    }

    [Fact]
    public async Task Excluded_Editors_Keep_Fallback_And_Print_Starts_No_Process()
    {
        using var fixture = new FakeEditorFixture();
        var first = fixture.Profile("first");
        var second = fixture.Profile("second");
        var service = fixture.Service(first, second);
        var result = await service.LaunchAsync(new() { WorkspaceRoot = fixture.Root, Action = EditorAction.OpenFile, Path = "test.cs", ExcludedEditors = ["first"], Print = true }, TestContext.Current.CancellationToken);
        Assert.True(result.Success);
        Assert.True(result.Printed);
        Assert.Equal("second", result.Editor?.Value);
        Assert.NotNull(result.PrintableCommand);
        Assert.False(File.Exists(fixture.Output));
        Assert.Equal("print", result.TerminalHost);
    }

    [Fact]
    public async Task Cancellation_Is_Observed_Before_Process_Start()
    {
        using var fixture = new FakeEditorFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var service = fixture.Service(fixture.Profile("fixture"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await service.LaunchAsync(new() { WorkspaceRoot = fixture.Root }, cancelled.Token));
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public void Global_Options_Merge_Configured_Defaults_And_Custom_Profiles()
    {
        var options = EditorOptions.From(new Dictionary<string, object?>
        {
            ["strict_match"] = true, ["focus"] = "never", ["fallback"] = "os-default",
            ["editors"] = new object?[]
            {
                "nvim",
                new Dictionary<string, object?> { ["id"] = "vscode", ["command"] = "configured-code" },
                new Dictionary<string, object?> { ["id"] = "custom", ["command"] = "custom-editor", ["actions"] = new Dictionary<string, object?> { ["open_file"] = new[] { "{path}:{line}:{column}" }, ["open_workspace"] = new[] { "{workspace}" } } },
                new Dictionary<string, object?> { ["id"] = "disabled-profile", ["enabled"] = false },
            },
        });
        Assert.Equal(["nvim", "vscode", "custom"], options.Editors.Select(d => d.Id.Value));
        Assert.True(options.StrictMatch);
        Assert.Equal(EditorFallback.OsDefault, options.Fallback);
        Assert.Equal("configured-code", options.Editors[1].Command);
        Assert.Empty(options.Editors[1].ExecutableCandidates);
        Assert.True(options.Editors[2].Actions.ContainsKey(EditorAction.OpenAtPosition));
    }

    [Fact]
    public async Task Trusted_Diff_Snapshot_Root_Does_Not_Allow_Opening_External_Targets()
    {
        using var fixture = new FakeEditorFixture();
        var snapshots = TempTree.CreateDirectory("trusted-editor-snapshots");
        try
        {
            var left = Path.Combine(snapshots, "old.cs");
            File.WriteAllText(left, "old");
            File.WriteAllText(Path.Combine(fixture.Root, "current.cs"), "current");
            var descriptor = fixture.Profile("diff");
            descriptor = descriptor with { Capabilities = descriptor.Capabilities | EditorCapability.Diff, Actions = descriptor.Actions.Concat(new[] { new KeyValuePair<EditorAction, string[]>(EditorAction.OpenDiff, ["{left}", "{right}"]) }).ToDictionary(p => p.Key, p => p.Value) };
            var catalog = new EditorCatalog([descriptor]);
            var service = new EditorLauncher(catalog, new() { Editors = [descriptor] }, trustedSnapshotRoots: [snapshots]);
            var request = new EditorRequest { WorkspaceRoot = fixture.Root, Action = EditorAction.OpenDiff, Left = left, Right = "current.cs", DryRun = true };
            Assert.True((await service.LaunchAsync(request, TestContext.Current.CancellationToken)).Success);
            Assert.Equal(EditorErrorCode.OUTSIDE_WORKSPACE, (await service.LaunchAsync(request with { Path = left }, TestContext.Current.CancellationToken)).Error);
            Assert.Equal(EditorErrorCode.OUTSIDE_WORKSPACE, (await new EditorLauncher(catalog, new() { Editors = [descriptor] }).LaunchAsync(request, TestContext.Current.CancellationToken)).Error);
        }
        finally { TempTree.Delete(snapshots); }
    }

    [Fact]
    public void Globs_Support_Recursive_And_Root_Files_Without_Regex_Syntax()
    {
        Assert.True(EditorSelection.Glob("**/*.cs", "File.cs"));
        Assert.True(EditorSelection.Glob("**/*.cs", "src/nested/File.cs"));
        Assert.False(EditorSelection.Glob("src/*.cs", "src/nested/File.cs"));
        Assert.True(EditorSelection.Glob("file[1].cs", "file[1].cs"));
        Assert.False(EditorSelection.Glob("file[1].cs", "file1.cs"));
    }

    [Fact]
    public async Task Windows_Batch_FakeEditor_Preserves_Percent_Bang_And_Metacharacters()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new FakeEditorFixture();
        var executable = Path.Combine(fixture.Root, "fake-editor.cmd");
        var powershell = EditorDetector.FindOnPath("powershell.exe")!;
        File.WriteAllText(executable, $"@echo off\r\n\"{powershell}\" -NoProfile -NonInteractive -File \"{Path.Combine(fixture.Root, "fake-editor.ps1")}\" %*\r\n");
        var descriptor = fixture.Profile("batch") with
        {
            Command = executable,
            Actions = new Dictionary<EditorAction, string[]> { [EditorAction.OpenFile] = ["{path}"] },
            Capabilities = EditorCapability.OpenFile | EditorCapability.Terminal,
        };
        var path = Path.Combine(fixture.Root, "literal & %PATH% !bang! (parentheses).cs");
        var result = await fixture.Service(descriptor).LaunchAsync(new() { WorkspaceRoot = fixture.Root, Editor = "batch", Action = EditorAction.OpenFile, Path = path }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Equal(path, File.ReadAllLines(fixture.Output)[0]);
    }

    [Fact]
    public async Task Injected_Dsh_Pty_Host_Precedes_System_Terminal()
    {
        using var fixture = new FakeEditorFixture();
        var descriptor = fixture.Profile("pty");
        var host = new DelegateTerminalHost("DSH-PTY-fixture", _ => true, async (command, token) =>
        {
            var result = await EditorProcess.StartAsync(command with { WaitForExit = true }, token);
            return result with { TerminalHost = "DSH-PTY-fixture" };
        });
        var service = new EditorLauncher(new EditorCatalog([descriptor]), new() { Editors = [descriptor] }, new EditorExecutor([host]));
        var result = await service.LaunchAsync(new() { WorkspaceRoot = fixture.Root, Action = EditorAction.OpenFile, Path = "fixture.cs" }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Equal("DSH-PTY-fixture", result.TerminalHost);
        Assert.True(File.Exists(fixture.Output));
    }

    [Fact]
    public async Task Print_Can_Render_Configured_Client_Command_Without_Local_Installation()
    {
        using var fixture = new FakeEditorFixture();
        var descriptor = fixture.Profile("client-editor") with { Command = Path.Combine(fixture.Root, "not-installed-editor") };
        var result = await fixture.Service(descriptor).LaunchAsync(new() { WorkspaceRoot = fixture.Root, Editor = "client-editor", Action = EditorAction.OpenFile, Path = "source.cs", Print = true }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.True(result.Printed);
        Assert.False(result.Decision.Steps[0].Installed);
        Assert.Equal("configured-print", result.Decision.Steps[0].Source);
        Assert.Contains("unverified", result.Decision.Steps[0].Reason);
        Assert.Contains("not-installed-editor", result.PrintableCommand);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public async Task Trigger_Order_And_Reuse_Focus_Policies_Render_Global_Templates()
    {
        using var fixture = new FakeEditorFixture();
        var first = fixture.Profile("first");
        var second = fixture.Profile("second") with { ReuseArguments = ["--reuse"], NewWindowArguments = ["--new"], BackgroundArguments = ["--background"], FocusArguments = ["--focus", "{file}"] };
        var service = fixture.Service(first, second);
        var request = new EditorRequest { WorkspaceRoot = fixture.Root, Action = EditorAction.OpenFile, Path = "source.cs", TriggerEditors = ["second", "first"], TriggerId = "manual", DryRun = true };
        var background = await service.LaunchAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("second", background.Editor?.Value);
        Assert.Equal("manual", background.Decision.TriggerId);
        Assert.Contains("--background", background.Arguments);
        var reuse = await service.LaunchAsync(request with { ReuseWindow = EditorReuseWindow.Always, Focus = EditorFocus.Always }, TestContext.Current.CancellationToken);
        Assert.Equal(["--reuse", "--focus", "source.cs"], reuse.Arguments[..3]);
        var unsupported = await service.LaunchAsync(request with { Editor = "first", ReuseWindow = EditorReuseWindow.Never }, TestContext.Current.CancellationToken);
        Assert.Equal(EditorErrorCode.UNSUPPORTED, unsupported.Error);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public async Task Dynamic_Plugin_Permission_Gates_Execution_But_Not_Print_Or_DryRun()
    {
        using var fixture = new FakeEditorFixture();
        var profile = fixture.Profile("permission");
        var allowed = false;
        var host = new DelegateTerminalHost("permission-fixture", _ => true, (command, token) => EditorProcess.StartAsync(command with { WaitForExit = true }, token));
        var service = new EditorLauncher(new EditorCatalog([profile]), new() { Editors = [profile] }, new EditorExecutor([host]), canLaunch: () => allowed);
        var request = new EditorRequest { WorkspaceRoot = fixture.Root, Action = EditorAction.OpenFile, Path = "source.cs" };
        Assert.Equal(EditorErrorCode.PERMISSION_DENIED, (await service.LaunchAsync(request, TestContext.Current.CancellationToken)).Error);
        Assert.True((await service.LaunchAsync(request with { DryRun = true }, TestContext.Current.CancellationToken)).Success);
        Assert.True((await service.LaunchAsync(request with { Print = true }, TestContext.Current.CancellationToken)).Success);
        Assert.False(File.Exists(fixture.Output));
        allowed = true;
        Assert.True((await service.LaunchAsync(request, TestContext.Current.CancellationToken)).Success);
        Assert.True(File.Exists(fixture.Output));
    }

    [Fact]
    public void Combined_Trigger_Conditions_Require_Workspace_And_File_Matches()
    {
        using var fixture = new FakeEditorFixture();
        var workspace = new EditorWorkspace(fixture.Root, [".idea"]);
        var conditions = new EditorConditions { Workspace = [".idea"], Extensions = [".cs"] };
        Assert.True(EditorSelection.MatchesAll(conditions, workspace, "source.cs"));
        Assert.False(EditorSelection.MatchesAll(conditions, workspace, "source.java"));
        Assert.False(EditorSelection.MatchesAll(conditions, workspace, null));
        Assert.False(EditorSelection.MatchesAll(conditions, new(fixture.Root, [".vscode"]), "source.cs"));
        Assert.True(EditorSelection.MatchesAll(new(), workspace, null));
    }

    [Fact]
    public async Task RemoteUri_FakeEditor_Records_Configured_VsCode_Cli_Arguments()
    {
        using var fixture = new FakeEditorFixture();
        var configured = EditorOptions.From(null).Editors.Single(d => d.Id.Value == "vscode");
        var descriptor = fixture.Profile("vscode") with { Command = fixture.Executable, Actions = configured.Actions, RemoteActions = configured.RemoteActions, Capabilities = configured.Capabilities | EditorCapability.Terminal };
        var service = fixture.Service(descriptor);
        const string uri = "vscode-remote://ssh-remote+fixture/workspace/source%20%26%20file.cs";
        var request = new EditorRequest { WorkspaceRoot = fixture.Root, Path = "source & file.cs", Editor = "vscode", Action = EditorAction.OpenFile, RemoteUri = uri };
        var file = await service.LaunchAsync(request, TestContext.Current.CancellationToken);
        Assert.True(file.Success, file.Message);
        Assert.Equal(["--file-uri", uri, fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
        var position = await service.LaunchAsync(request with { Action = EditorAction.OpenAtPosition, Line = 42, Column = 7 }, TestContext.Current.CancellationToken);
        Assert.True(position.Success, position.Message);
        Assert.Equal(["--goto", "--file-uri", $"{uri}:42:7", fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
        const string workspaceUri = "vscode-remote://wsl+fixture/workspace";
        var workspace = await service.LaunchAsync(request with { Action = EditorAction.OpenWorkspace, Path = fixture.Root, RemoteUri = workspaceUri }, TestContext.Current.CancellationToken);
        Assert.True(workspace.Success, workspace.Message);
        Assert.Equal(["--folder-uri", workspaceUri, fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
        Assert.Equal(EditorErrorCode.OUTSIDE_WORKSPACE, (await service.LaunchAsync(request with { Path = "../outside.cs" }, TestContext.Current.CancellationToken)).Error);
    }

    [Theory]
    [InlineData("https://fixture/workspace/source.cs")]
    [InlineData("vscode-remote://user@fixture/workspace/source.cs")]
    [InlineData("vscode-remote://fixture:22/workspace/source.cs")]
    [InlineData("vscode-remote://fixture/workspace/source.cs?command=unexpected")]
    [InlineData("vscode-remote://fixture/workspace/source.cs#fragment")]
    [InlineData("vscode-remote://fixture/workspace/%2e%2e/source.cs")]
    [InlineData("vscode-remote://fixture/workspace/%0asource.cs")]
    [InlineData("vscode-remote://fixture/workspace/source%2fother.cs")]
    public async Task RemoteUri_Rejects_Unallowlisted_Or_Nonnormalized_Targets(string uri)
    {
        using var fixture = new FakeEditorFixture();
        var descriptor = fixture.Profile("vscode");
        var result = await fixture.Service(descriptor).LaunchAsync(new() { WorkspaceRoot = fixture.Root, Path = "source.cs", Action = EditorAction.OpenFile, RemoteUri = uri, DryRun = true }, TestContext.Current.CancellationToken);
        Assert.Equal(EditorErrorCode.INVALID_CONFIGURATION, result.Error);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public async Task RemoteUri_Requires_Remote_Capability_And_Does_Not_Fall_Back_To_Local_Os()
    {
        using var fixture = new FakeEditorFixture();
        var descriptor = fixture.Profile("ordinary");
        var service = fixture.Service(new EditorOptions { Editors = [descriptor], Fallback = EditorFallback.OsDefault }, descriptor);
        var request = new EditorRequest { WorkspaceRoot = fixture.Root, Path = "source.cs", Action = EditorAction.OpenFile, Editor = "ordinary", RemoteUri = "vscode-remote://dev-container+fixture/workspace/source.cs", DryRun = true };
        Assert.Equal(EditorErrorCode.UNSUPPORTED, (await service.LaunchAsync(request, TestContext.Current.CancellationToken)).Error);
        Assert.False((await service.LaunchAsync(request with { Editor = null }, TestContext.Current.CancellationToken)).Success);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public async Task Configuration_Order_And_Disabled_Entries_Control_The_Launched_Editor()
    {
        using var fixture = new FakeEditorFixture();
        var environment = fixture.Profile("recording").Environment;
        var options = EditorOptions.From(new Dictionary<string, object?>
        {
            ["editors"] = new[] { ("disabled", false), ("second", true), ("first", true) }.Select(entry => new Dictionary<string, object?>
            {
                ["id"] = entry.Item1, ["enabled"] = entry.Item2, ["command"] = fixture.Executable, ["kind"] = "terminal",
                ["env"] = environment, ["actions"] = new Dictionary<string, object?> { ["open_file"] = new[] { entry.Item1, "{path}" } },
            }).ToArray(),
        });
        var result = await fixture.Service(options, options.Editors.ToArray()).LaunchAsync(new()
        {
            WorkspaceRoot = fixture.Root, Path = "source.cs", Action = EditorAction.OpenFile,
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Equal(["second", Path.Combine(fixture.Root, "source.cs"), fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
    }

    [Fact]
    public async Task Explicit_Empty_Editor_List_Does_Not_Restore_Default_Profiles()
    {
        using var fixture = new FakeEditorFixture();
        var options = EditorOptions.From(new Dictionary<string, object?> { ["editors"] = Array.Empty<object>() });
        var result = await fixture.Service(options, options.Editors.ToArray()).LaunchAsync(new()
        {
            WorkspaceRoot = fixture.Root, Path = "source.cs", Action = EditorAction.OpenFile,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(EditorErrorCode.NOT_FOUND, result.Error);
    }

    [Fact]
    public void Configured_Action_Rejects_An_Executable_Placeholder()
    {
        var options = EditorOptions.From(new Dictionary<string, object?>
        {
            ["editors"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "invalid-template", ["command"] = "fixture-configured-editor",
                    ["actions"] = new Dictionary<string, object?> { ["open_file"] = new[] { "{command}" } },
                },
            },
        });
        Assert.Throws<ArgumentException>(() => new EditorCatalog(options.Editors));
    }

    [Fact]
    public async Task Explicit_Empty_Actions_Remove_Default_Launch_Support()
    {
        using var fixture = new FakeEditorFixture();
        var options = EditorOptions.From(new Dictionary<string, object?>
        {
            ["editors"] = new object[]
            {
                new Dictionary<string, object?> { ["id"] = "vim", ["command"] = fixture.Executable, ["actions"] = new Dictionary<string, object?>() },
            },
        });
        var result = await fixture.Service(options, options.Editors.ToArray()).LaunchAsync(new()
        {
            WorkspaceRoot = fixture.Root, Path = "source.cs", Action = EditorAction.OpenFile,
        }, TestContext.Current.CancellationToken);
        Assert.Equal(EditorErrorCode.UNSUPPORTED, Assert.Single(result.Decision.Steps).Error);
    }

    [Theory]
    [InlineData(EditorAction.OpenFile, "source.cs")]
    [InlineData(EditorAction.OpenWorkspace, null)]
    public async Task Explicit_Empty_Conditions_Remove_Default_Workspace_And_Extension_Preferences(EditorAction action, string? path)
    {
        using var fixture = new FakeEditorFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".idea"));
        var environment = fixture.Profile("recording").Environment;
        var options = EditorOptions.From(new Dictionary<string, object?>
        {
            ["editors"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "first", ["command"] = fixture.Executable, ["kind"] = "terminal", ["env"] = environment,
                    ["actions"] = new Dictionary<string, object?> { ["open_file"] = new[] { "first", "{path}" }, ["open_workspace"] = new[] { "first", "{workspace}" } },
                },
                new Dictionary<string, object?>
                {
                    ["id"] = "rider", ["command"] = fixture.Executable, ["kind"] = "terminal",
                    ["env"] = environment, ["when"] = new Dictionary<string, object?>(),
                },
            },
        });
        var result = await fixture.Service(options, options.Editors.ToArray()).LaunchAsync(new()
        {
            WorkspaceRoot = fixture.Root, Path = path, Action = action,
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Equal("first", result.Editor?.Value);
        Assert.Equal("first", File.ReadAllLines(fixture.Output)[0]);
    }

    [Fact]
    public async Task Configured_Installation_Root_Is_Used_To_Launch_An_Editor_Outside_Path()
    {
        using var fixture = new FakeEditorFixture();
        var descriptor = fixture.Profile("configured-location") with { Command = Path.GetFileName(fixture.Executable) };
        var options = EditorOptions.From(new Dictionary<string, object?>
        {
            ["installation_roots"] = new Dictionary<string, object?>
            {
                ["windows"] = new[] { fixture.Root }, ["macos"] = new[] { fixture.Root }, ["linux"] = new[] { fixture.Root },
            },
        }) with { Editors = [descriptor] };
        var result = await fixture.Service(options, descriptor).LaunchAsync(new()
        {
            WorkspaceRoot = fixture.Root, Path = "source.cs", Action = EditorAction.OpenFile,
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Equal([Path.Combine(fixture.Root, "source.cs"), fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
    }

    [Fact]
    public async Task Configured_Discovery_Command_On_Path_Locates_And_Launches_An_Editor()
    {
        using var fixture = new FakeEditorFixture();
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var command = OperatingSystem.IsWindows() ? "powershell.exe" : "sh";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["-NoProfile", "-NonInteractive", "-Command", $"[Console]::WriteLine('{fixture.Executable.Replace("'", "''")}')"]
            : ["-c", "printf '%s\\n' \"$1\"", "dsh-discovery", fixture.Executable];
        var descriptor = fixture.Profile("configured-discovery") with
        {
            Command = $"uninstalled-editor-{Guid.NewGuid():N}",
            DiscoveryCommands = [new(platform, command, arguments)],
        };
        var result = await fixture.Service(descriptor).LaunchAsync(new()
        {
            WorkspaceRoot = fixture.Root, Path = "source.cs", Action = EditorAction.OpenFile,
        }, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Equal([Path.Combine(fixture.Root, "source.cs"), fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
    }

    [Fact]
    public async Task Remote_Mapping_Uses_Configured_Action_Templates_Without_A_Built_In_Editor_Id()
    {
        using var fixture = new FakeEditorFixture();
        var configuration = new Dictionary<string, object?>
        {
            ["editors"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "configured-remote", ["command"] = fixture.Executable, ["kind"] = "terminal",
                    ["env"] = fixture.Profile("recording").Environment, ["remote"] = true,
                    ["actions"] = new Dictionary<string, object?> { ["open_file"] = new[] { "{path}" } },
                    ["remote_actions"] = new Dictionary<string, object?> { ["open_file"] = new[] { "configured-uri", "{remote_uri}" } },
                },
            },
            ["client_mappings"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["machine"] = "remote", ["source_root"] = "/workspace", ["destination_root"] = fixture.Root,
                    ["kind"] = "ssh", ["authority"] = "fixture",
                },
            },
        };
        var options = EditorOptions.From(configuration);
        var request = EditorPathMapper.From(configuration).Map("remote", new()
        {
            WorkspaceRoot = "/workspace", Path = "source.cs", Action = EditorAction.OpenFile,
        });
        var result = await fixture.Service(options, options.Editors.ToArray()).LaunchAsync(request, TestContext.Current.CancellationToken);
        Assert.True(result.Success, result.Message);
        Assert.Equal(["configured-uri", "vscode-remote://ssh-remote+fixture/workspace/source.cs", fixture.Root, "fixture"], File.ReadAllLines(fixture.Output));
    }

    internal sealed class FakeEditorFixture : IDisposable
    {
        public string Root { get; } = TempTree.CreateDirectory("fake-editor-argv-cwd-env");
        public string Output => Path.Combine(Root, "recorded.txt");
        private string Script => Path.Combine(Root, OperatingSystem.IsWindows() ? "fake-editor.ps1" : "fake-editor.sh");
        public string Executable => OperatingSystem.IsWindows() ? Path.Combine(Root, "fake-editor.cmd") : Script;
        public FakeEditorFixture()
        {
            if (OperatingSystem.IsWindows())
                File.WriteAllText(Script, "if ($args[0] -eq 'fail') { exit 7 }\n[System.IO.File]::WriteAllLines($env:DSH_FAKE_EDITOR_OUTPUT, [string[]](@($args) + @((Get-Location).Path, $env:DSH_FAKE_EDITOR_VALUE)))\n");
            else File.WriteAllText(Script, "#!/bin/sh\nif [ \"$1\" = fail ]; then exit 7; fi\n{ printf '%s\\n' \"$@\"; pwd -P; printf '%s\\n' \"$DSH_FAKE_EDITOR_VALUE\"; } > \"$DSH_FAKE_EDITOR_OUTPUT\"\n");
            if (OperatingSystem.IsWindows())
                File.WriteAllText(Executable, $"@echo off\r\n\"{CatalogCommand()}\" -NoProfile -NonInteractive -File \"{Script}\" %*\r\n");
            else File.SetUnixFileMode(Script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        public EditorDescriptor Profile(string id, bool fail = false)
        {
            return new()
            {
                Id = id, Kind = EditorKind.Terminal, Command = Executable,
                Capabilities = EditorCapability.OpenFile | EditorCapability.OpenAtPosition | EditorCapability.OpenWorkspace | EditorCapability.Terminal,
                Actions = new Dictionary<EditorAction, string[]> { [EditorAction.OpenFile] = [.. fail ? new[] { "fail" } : Array.Empty<string>(), "{path}"], [EditorAction.OpenAtPosition] = [.. fail ? new[] { "fail" } : Array.Empty<string>(), "{path}", "{line}", "{column}"], [EditorAction.OpenWorkspace] = ["{workspace}"] },
                Environment = new Dictionary<string, string> { ["DSH_FAKE_EDITOR_OUTPUT"] = Output, ["DSH_FAKE_EDITOR_VALUE"] = "fixture" },
            };
        }
        private static string CatalogCommand() => EditorDetector.FindOnPath("powershell.exe") ?? throw new InvalidOperationException("Windows PowerShell is required for the explicitly labeled fake-editor argv/cwd/env fixture.");
        public EditorLauncher Service(params EditorDescriptor[] profiles) => Service(new() { Editors = profiles }, profiles);
        public EditorLauncher Service(EditorOptions options, params EditorDescriptor[] profiles)
        {
            var host = new DelegateTerminalHost("fake-editor-argv-cwd-env", _ => true, (command, token) => EditorProcess.StartAsync(command with { WaitForExit = true }, token));
            return new(new EditorCatalog(profiles, new EditorDetector(options.InstallationRoots, options.BundleRoots, options.DesktopRoots)), options, new EditorExecutor([host]));
        }
        public void Dispose() => TempTree.Delete(Root);
    }
}
