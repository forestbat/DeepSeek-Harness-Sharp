using Dsh.Core;
using Dsh.Ide;
using Dsh.Ide.Catalog;
using Dsh.Ide.Launch;
using Dsh.Ide.Triggers;

namespace Dsh.Tests;

public sealed class EditorTriggerTests
{
    [Fact]
    public async Task First_Falls_Through_Failed_Process_And_Stops_Later_Rules()
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var laterOutput = Path.Combine(fixture.Root, "later.txt");
        var later = fixture.Profile("later") with { Environment = new Dictionary<string, string> { ["DSH_FAKE_EDITOR_OUTPUT"] = laterOutput } };
        var service = fixture.Service(fixture.Profile("failed", fail: true), fixture.Profile("working"), later);
        var rules = new EditorTrigger[]
        {
            new() { Id = "first", On = "manual", Action = EditorAction.OpenFile, Editors = ["failed", "working"] },
            new() { Id = "later", On = "manual", Action = EditorAction.OpenFile, Editors = ["later"] },
        };
        var outcomes = await new EditorTriggerEngine(service, rules).ExecuteAsync(new("manual", new() { WorkspaceRoot = fixture.Root, Path = "source.cs", SessionId = "session" }), TestContext.Current.CancellationToken);
        var outcome = Assert.Single(outcomes);
        Assert.Equal("first", outcome.Rule);
        var result = Assert.Single(outcome.Results);
        Assert.Equal("working", result.Editor?.Value);
        Assert.Equal(EditorErrorCode.LAUNCH_FAILED, result.Decision.Steps.Single(step => step.Editor.Value == "failed").Error);
        Assert.Equal(Path.Combine(fixture.Root, "source.cs"), File.ReadAllLines(fixture.Output)[0]);
        Assert.False(File.Exists(laterOutput));
    }

    [Fact]
    public async Task All_Executes_Only_Declared_Editors_In_Their_Order()
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var profiles = new[] { "a", "b", "unselected" }.Select(id => fixture.Profile(id) with { Environment = new Dictionary<string, string> { ["DSH_FAKE_EDITOR_OUTPUT"] = Path.Combine(fixture.Root, $"{id}.txt") } }).ToArray();
        var rule = new EditorTrigger { Id = "both", On = "manual", Action = EditorAction.OpenFile, Editors = ["b", "a"], Mode = EditorTriggerMode.All };
        var outcomes = await new EditorTriggerEngine(fixture.Service(profiles), [rule]).ExecuteAsync(new("manual", new() { WorkspaceRoot = fixture.Root, Path = "source.cs", SessionId = "session" }), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "b", "a" }, Assert.Single(outcomes).Results.Select(result => result.Editor!.Value.Value));
        Assert.Equal(Path.Combine(fixture.Root, "source.cs"), File.ReadAllLines(Path.Combine(fixture.Root, "b.txt"))[0]);
        Assert.Equal(Path.Combine(fixture.Root, "source.cs"), File.ReadAllLines(Path.Combine(fixture.Root, "a.txt"))[0]);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "unselected.txt")));
    }

    [Fact]
    public async Task Once_Turn_And_Cooldown_Do_Not_Become_Permanent_Suppression()
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var clock = new TriggerClock();
        var rule = new EditorTrigger { Id = "write", On = "tool.after_write", Action = EditorAction.OpenFile, Once = EditorTriggerOnce.Turn, Cooldown = TimeSpan.FromMinutes(1) };
        var engine = new EditorTriggerEngine(fixture.Service(fixture.Profile("editor")), [rule], clock);
        var context = new EditorTriggerContext("tool.after_write", new() { WorkspaceRoot = fixture.Root, Path = "source.cs", SessionId = "session", TurnId = "1" });
        Assert.True(Assert.Single(Assert.Single(await engine.ExecuteAsync(context, TestContext.Current.CancellationToken)).Results).Success);
        File.Delete(fixture.Output);
        Assert.Equal("cooldown", Assert.Single(await engine.ExecuteAsync(context with { Request = context.Request with { TurnId = "2" } }, TestContext.Current.CancellationToken)).Reason);
        Assert.False(File.Exists(fixture.Output));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal("once", Assert.Single(await engine.ExecuteAsync(context, TestContext.Current.CancellationToken)).Reason);
        Assert.False(File.Exists(fixture.Output));
        Assert.True(Assert.Single(Assert.Single(await engine.ExecuteAsync(context with { Request = context.Request with { TurnId = "2" } }, TestContext.Current.CancellationToken)).Results).Success);
        Assert.Equal(Path.Combine(fixture.Root, "source.cs"), File.ReadAllLines(fixture.Output)[0]);
    }

    [Fact]
    public async Task Printed_Fallback_Does_Not_Consume_Once_Before_A_Host_Becomes_Available()
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var available = false;
        var profile = fixture.Profile("editor");
        var terminal = new DelegateTerminalHost("late-pty", _ => available, (command, signal) => EditorProcess.StartAsync(command with { WaitForExit = true }, signal));
        var service = new EditorLauncher(new EditorCatalog([profile]), new() { Editors = [profile] }, new EditorExecutor([terminal, new PrintTerminalHost()]));
        var rule = new EditorTrigger { Id = "start", On = "session.start", Action = EditorAction.OpenWorkspace, Once = EditorTriggerOnce.Workspace };
        var engine = new EditorTriggerEngine(service, [rule]);
        var context = new EditorTriggerContext("session.start", new() { WorkspaceRoot = fixture.Root, SessionId = "session" });
        Assert.True(Assert.Single(Assert.Single(await engine.ExecuteAsync(context, TestContext.Current.CancellationToken)).Results).Printed);
        Assert.False(File.Exists(fixture.Output));
        available = true;
        Assert.False(Assert.Single(Assert.Single(await engine.ExecuteAsync(context, TestContext.Current.CancellationToken)).Results).Printed);
        Assert.Equal(fixture.Root, File.ReadAllLines(fixture.Output)[0]);
    }

    [Fact]
    public async Task Conditions_Are_Conjunctive_And_Workspace_Can_Disable_Triggers()
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".idea"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src"));
        var target = Path.Combine(fixture.Root, "src", "source.cs");
        File.WriteAllText(target, "fixture input");
        var rule = new EditorTrigger
        {
            Id = "source", On = "session.start", Action = EditorAction.OpenFile,
            When = new() { Target = new() { Workspace = [".idea"], Extensions = [".cs"], Globs = ["src/**"] }, ProjectTypes = ["dotnet"], MachineTypes = ["local"], HostEditors = ["vscode"], SessionSources = ["startup"] },
        };
        var engine = new EditorTriggerEngine(fixture.Service(fixture.Profile("editor")), [rule]);
        var context = new EditorTriggerContext("session.start", new() { WorkspaceRoot = fixture.Root, Path = target, SessionId = "session", HostEditor = "vscode" }) { SessionSource = "resume", ProjectTypes = ["dotnet"] };
        Assert.Empty(await engine.ExecuteAsync(context, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(fixture.Output));
        Assert.True(Assert.Single(Assert.Single(await engine.ExecuteAsync(context with { SessionSource = "startup" }, TestContext.Current.CancellationToken)).Results).Success);
        File.Delete(fixture.Output);
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".dsh"));
        File.WriteAllText(Path.Combine(fixture.Root, ".dsh", "ide.yaml"), "triggers: false\n");
        Assert.Empty(await engine.ExecuteAsync(context with { SessionSource = "startup", Request = context.Request with { SessionId = "another" } }, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(fixture.Output));
    }

    [Theory]
    [InlineData("src/source.java")]
    [InlineData("source.cs")]
    public async Task Every_File_Condition_Is_Required_Before_An_Automatic_Launch(string relativePath)
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "src"));
        var path = Path.Combine(fixture.Root, relativePath);
        File.WriteAllText(path, "fixture input");
        var rule = new EditorTrigger
        {
            Id = "source", On = "tool.after_write", Action = EditorAction.OpenFile,
            When = new() { Target = new() { Extensions = [".cs"], Globs = ["src/**"] } },
        };
        var context = new EditorTriggerContext("tool.after_write", new() { WorkspaceRoot = fixture.Root, Path = path });
        var outcomes = await new EditorTriggerEngine(fixture.Service(fixture.Profile("editor")), [rule]).ExecuteAsync(context, TestContext.Current.CancellationToken);
        Assert.Empty(outcomes);
        Assert.False(File.Exists(fixture.Output));
    }

    [Fact]
    public async Task Configured_Host_Rules_Work_When_The_Host_Is_Not_A_Configured_Launch_Target()
    {
        using var fixture = new EditorLauncherTests.FakeEditorFixture();
        var variable = $"DSH_IDE_TEST_HOST_{Guid.NewGuid():N}";
        var options = EditorOptions.From(new Dictionary<string, object?>
        {
            ["host_detection"] = new object[]
            {
                new Dictionary<string, object?> { ["editor"] = "configured-host", ["variable"] = variable, ["pattern"] = "*fixture/tool*" },
            },
        });
        var service = fixture.Service(fixture.Profile("configured-editor"));
        var rule = new EditorTrigger { Id = "configured-host", On = "manual", Action = EditorAction.OpenFile, When = new() { HostEditors = ["configured-host"] } };
        var context = new EditorTriggerContext("manual", new() { WorkspaceRoot = fixture.Root, Path = "source.cs" });
        try
        {
            Environment.SetEnvironmentVariable(variable, "protocol://fixture/tool/socket");
            var results = await new EditorTriggerEngine(service, [rule], hostDetection: options.HostDetection).ExecuteAsync(context, TestContext.Current.CancellationToken);
            Assert.True(Assert.Single(Assert.Single(results).Results).Success);
            Assert.Empty(await new EditorTriggerEngine(service, [rule], hostDetection: []).ExecuteAsync(context, TestContext.Current.CancellationToken));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private sealed class TriggerClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
