using System.Text.Json;
using Dsh.Boot;
using Dsh.Core;
using System.Text.Json.Serialization;
using Dsh.Ide.Triggers;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class EditorWorkflowTests
{
    private static readonly JsonSerializerOptions ResultOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Theory]
    [InlineData("command")]
    [InlineData("tool")]
    [InlineData("diff")]
    public async Task Manual_Entrypoints_Apply_Rules_With_Actual_Turns(string entrypoint)
    {
        using var fixture = await CreateFixtureAsync(entrypoint == "diff" ? "open_diff" : "open_file");
        fixture.Repository.Commit(("source.cs", "before"));
        fixture.Repository.Write("source.cs", "after");
        var agents = fixture.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Create(new CreateAgentOptions(Cwd: fixture.Repository.Root), TestContext.Current.CancellationToken);
        using var lease = handle.Dispose;
        var agent = handle.Agent;
        foreach (var turn in new[] { 17, 18 })
        {
            agent.Session.Append(new TurnStartPayload(turn));
            EditorResult result;
            if (entrypoint == "tool")
            {
                var tools = fixture.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
                var executed = await tools.Execute(new ToolExecutionInput
                {
                    Name = "ide", CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Agent = agent,
                    Arguments = JsonSerializer.SerializeToElement(new { action = "open", path = "source.cs" }),
                    Signal = TestContext.Current.CancellationToken,
                });
                Assert.False(executed.IsError);
                result = JsonSerializer.Deserialize<EditorResult>(Assert.IsType<ToolExecutionResult.Success>(executed).Value, ResultOptions)!;
            }
            else
            {
                var commands = fixture.App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
                var executed = await commands.Execute(agent, entrypoint == "diff" ? "/ide diff --git" : "/ide open source.cs", TestContext.Current.CancellationToken);
                result = JsonSerializer.Deserialize<EditorResult>(Assert.IsType<CommandResult.Success>(executed!.Result).Text!, ResultOptions)!;
            }
            Assert.True(result.Success, result.Message);
            Assert.Equal("selected", result.Editor?.Value);
            Assert.Equal("configured", result.Decision.TriggerId);
            var recorded = File.ReadAllLines(fixture.Editor.Output);
            if (entrypoint == "diff")
            {
                Assert.Equal("before", File.ReadAllText(recorded[0]));
                Assert.Equal("after", File.ReadAllText(recorded[1]));
            }
            else
                Assert.Equal(Path.Combine(fixture.Repository.Root, "source.cs"), recorded[0]);
            Assert.False(File.Exists(Path.Combine(fixture.Editor.Root, "global.txt")));
            agent.Session.Append(new TurnEndPayload(turn, new TurnEndReason.Completed()));
        }
    }

    [Fact(Timeout = 10000)]
    public async Task Error_Message_Location_Is_Used_Even_When_The_Exception_Has_A_StackTrace()
    {
        using var fixture = await CreateFixtureAsync("open_file", "agent.error");
        fixture.Repository.Write("source.cs", "source");
        var handle = await fixture.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(Cwd: fixture.Repository.Root), TestContext.Current.CancellationToken);
        using var lease = handle.Dispose;
        try
        {
            throw new InvalidOperationException("source.cs:42:7: compilation failed");
        }
        catch (InvalidOperationException error)
        {
            fixture.App.Ctx.Emit(new AgentErrorNotification(handle.Agent, 1, 1, error));
        }
        var launched = await fixture.FirstLaunch.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(launched.Success, launched.Message);
        Assert.Equal([Path.Combine(fixture.Repository.Root, "source.cs"), "42", "7", fixture.Repository.Root, "fixture"], File.ReadAllLines(fixture.Editor.Output));
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structured_Test_Failures_Open_The_Location_Regardless_Of_Tool_Error_Status(bool isError)
    {
        using var fixture = await CreateFixtureAsync("open_file", "test.failure");
        fixture.Repository.Write("source.cs", "source");
        var handle = await fixture.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(Cwd: fixture.Repository.Root), TestContext.Current.CancellationToken);
        using var lease = handle.Dispose;
        var callId = ToolCallId.Create(Guid.NewGuid().ToString("N"));
        var run = new ToolRunContext
        {
            Name = "test-runner", CallId = callId, RootCallIdValue = callId, Agent = handle.Agent,
            Arguments = JsonSerializer.SerializeToElement(new { }), Signal = TestContext.Current.CancellationToken,
        };
        var metadata = JsonSerializer.SerializeToElement(new { testFailures = new[] { new { path = "source.cs", line = 42, column = 7 } } });
        ToolExecutionResult result = isError
            ? new ToolExecutionResult.Failure { IsError = true, Error = new("One test failed."), Content = [new TextBlock("One test failed.")], Meta = metadata }
            : new ToolExecutionResult.Success { IsError = false, Value = JsonSerializer.SerializeToElement(new { failed = 1 }), Content = [new TextBlock("One test failed.")], Meta = metadata };
        fixture.App.Ctx.Emit(new ToolResultNotification(run, result));
        var launched = await fixture.FirstLaunch.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(launched.Success, launched.Message);
        Assert.Equal([Path.Combine(fixture.Repository.Root, "source.cs"), "42", "7", fixture.Repository.Root, "fixture"], File.ReadAllLines(fixture.Editor.Output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Model_Print_And_DryRun_Keep_Global_Environment_Private_While_Actual_Launch_Uses_It(bool dryRun)
    {
        const string canary = "synthetic-private-editor-environment-canary";
        using var fixture = await CreateFixtureAsync("open_file", environmentValue: canary);
        fixture.Repository.Write("source.cs", "source");
        var handle = await fixture.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(Cwd: fixture.Repository.Root), TestContext.Current.CancellationToken);
        using var lease = handle.Dispose;
        var tools = fixture.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var preview = await tools.Execute(new ToolExecutionInput
        {
            Name = "ide", CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Agent = handle.Agent,
            Arguments = JsonSerializer.SerializeToElement(new { action = "open", path = "source.cs", editor = "selected", print = !dryRun, dry_run = dryRun }),
            Signal = TestContext.Current.CancellationToken,
        });
        var previewResult = JsonSerializer.Deserialize<EditorResult>(Assert.IsType<ToolExecutionResult.Success>(preview).Value, ResultOptions)!;
        Assert.True(previewResult.Success, previewResult.Message);
        Assert.True(previewResult.Printed);
        Assert.Contains("source.cs", previewResult.PrintableCommand);
        Assert.DoesNotContain(canary, string.Join('\n', preview.Content.OfType<TextBlock>().Select(block => block.Text)), StringComparison.Ordinal);
        Assert.False(File.Exists(fixture.Editor.Output));
        var opened = await tools.Execute(new ToolExecutionInput
        {
            Name = "ide", CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Agent = handle.Agent,
            Arguments = JsonSerializer.SerializeToElement(new { action = "open", path = "source.cs", editor = "selected" }),
            Signal = TestContext.Current.CancellationToken,
        });
        var openedResult = JsonSerializer.Deserialize<EditorResult>(Assert.IsType<ToolExecutionResult.Success>(opened).Value, ResultOptions)!;
        Assert.True(openedResult.Success, openedResult.Message);
        Assert.Equal(canary, File.ReadAllLines(fixture.Editor.Output)[^1]);
    }

    [Fact]
    public async Task Model_Reveal_Cannot_Select_A_Workspace_Program_Through_Os_Default()
    {
        using var fixture = await CreateFixtureAsync("open_file", "test.failure", fallback: "os-default");
        fixture.Repository.Write("untrusted.cmd", "@echo off\r\nexit /b 0\r\n");
        var handle = await fixture.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!.Create(new CreateAgentOptions(Cwd: fixture.Repository.Root), TestContext.Current.CancellationToken);
        using var lease = handle.Dispose;
        var tools = fixture.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!;
        var executed = await tools.Execute(new ToolExecutionInput
        {
            Name = "ide", CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Agent = handle.Agent,
            Arguments = JsonSerializer.SerializeToElement(new { action = "reveal", path = "untrusted.cmd", dry_run = true }),
            Signal = TestContext.Current.CancellationToken,
        });
        var result = JsonSerializer.Deserialize<EditorResult>(Assert.IsType<ToolExecutionResult.Success>(executed).Value, ResultOptions)!;
        Assert.False(result.Success);
        Assert.Equal(EditorErrorCode.NOT_FOUND, result.Error);
        Assert.Null(result.PrintableCommand);
        var commands = fixture.App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        var user = await commands.Execute(handle.Agent, "/ide reveal untrusted.cmd --print", TestContext.Current.CancellationToken);
        var userResult = JsonSerializer.Deserialize<EditorResult>(Assert.IsType<CommandResult.Success>(user!.Result).Text!, ResultOptions)!;
        Assert.True(userResult.Success, userResult.Message);
        Assert.True(userResult.Printed);
        Assert.Contains("untrusted.cmd", userResult.PrintableCommand);
    }

    private static async Task<Fixture> CreateFixtureAsync(string action, string on = "manual", string environmentValue = "fixture", string fallback = "none")
    {
        var repository = new GitTestRepository();
        var editor = new EditorLauncherTests.FakeEditorFixture();
        var homePath = Directory.CreateDirectory(Path.Combine(repository.Sandbox, "home")).FullName;
        var command = JsonSerializer.Serialize(editor.Executable);
        var selectedOutput = JsonSerializer.Serialize(editor.Output);
        var globalOutput = JsonSerializer.Serialize(Path.Combine(editor.Root, "global.txt"));
        File.WriteAllText(Path.Combine(homePath, "settings.yaml"), $$"""
            plugins:
              "@deepseek-ai/dsh-ide":
                fallback: {{fallback}}
                editors:
                  - id: global
                    kind: terminal
                    command: {{command}}
                    actions:
                      open_file: ["{path}"]
                      open_at_position: ["{path}", "{line}", "{column}"]
                      open_diff: ["{left}", "{right}"]
                    env:
                      DSH_FAKE_EDITOR_OUTPUT: {{globalOutput}}
                      DSH_FAKE_EDITOR_VALUE: {{JsonSerializer.Serialize(environmentValue)}}
                  - id: selected
                    kind: terminal
                    command: {{command}}
                    actions:
                      open_file: ["{path}"]
                      open_at_position: ["{path}", "{line}", "{column}"]
                      open_diff: ["{left}", "{right}"]
                    env:
                      DSH_FAKE_EDITOR_OUTPUT: {{selectedOutput}}
                      DSH_FAKE_EDITOR_VALUE: {{JsonSerializer.Serialize(environmentValue)}}
                triggers:
                  - id: configured
                    on: {{on}}
                    action: {{action}}
                    editors: [selected]
                    once: turn
            """);
        try
        {
            var app = await ConfigBoot.Compose(new HarnessOptions(HarnessHome.Resolve(homePath), Cwd: repository.Root, PluginsDirectory: Path.Combine(repository.Sandbox, "plugins")));
            var firstLaunch = new TaskCompletionSource<EditorExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            app.Ctx.Provide("editorTerminalHost", new Dsh.Ide.Launch.DelegateTerminalHost("fake-editor-argv-cwd-env", _ => true,
                async (launch, signal) =>
                {
                    var result = await Dsh.Ide.Launch.EditorProcess.StartAsync(launch with { WaitForExit = true }, signal);
                    firstLaunch.TrySetResult(result);
                    return result;
                }));
            return new(app, repository, editor, firstLaunch.Task);
        }
        catch
        {
            editor.Dispose();
            repository.Dispose();
            throw;
        }
    }

    private sealed record Fixture(HarnessApp App, GitTestRepository Repository, EditorLauncherTests.FakeEditorFixture Editor, Task<EditorExecutionResult> FirstLaunch) : IDisposable
    {
        public void Dispose()
        {
            App.Dispose();
            Editor.Dispose();
            Repository.Dispose();
        }
    }
}
