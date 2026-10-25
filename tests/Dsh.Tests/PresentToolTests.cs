using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Deliverables;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Tests;

/** present 工具与交付物清单: 真实插件组合 + 真实 JSONL 持久化, 测试只组装文件与参数并断言会话事件。 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class PresentToolTests
{
    private const string ToolName = "present";

    [Fact]
    public async Task Present_ExistingFile_RecordsAbsolutePathAndRendersIt()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        var expected = await CreateFile(session, "report.docx");
        session.Append(new TurnStartPayload(1));

        var result = await Present(environment, File("report.docx", "季度报告"));

        Assert.False(result.IsError, TextOf(result));
        Assert.Equal($"Presented {expected}", TextOf(result));
        Assert.Equal([new PresentedFile(expected, "季度报告")], SessionDeliverables.Collect(session.SnapshotEvents()));
        var presented = Assert.IsType<DeliverablesPresentedPayload>(session.SnapshotEvents().Last(IsPresented).Data);
        Assert.Equal(1, presented.Turn);
    }

    [Fact]
    public async Task Present_MissingFile_FailsWithoutRecording()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        session.Append(new TurnStartPayload(1));

        var result = await Present(environment, File("missing.xlsx"));

        Assert.True(result.IsError);
        Assert.Contains("file not found", TextOf(result));
        Assert.DoesNotContain(session.SnapshotEvents(), IsPresented);
    }

    [Fact]
    public async Task Present_Directory_FailsWithoutRecording()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        Directory.CreateDirectory(Path.Combine(session.Header.Cwd!, "out"));
        session.Append(new TurnStartPayload(1));

        var result = await Present(environment, File("out"));

        Assert.True(result.IsError);
        Assert.Contains("not a regular file", TextOf(result));
        Assert.DoesNotContain(session.SnapshotEvents(), IsPresented);
    }

    [Fact]
    public async Task Present_OutsideOpenTurn_Fails()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        await CreateFile(environment.Agent.Session, "notes.md");

        var result = await Present(environment, File("notes.md"));

        Assert.True(result.IsError);
        Assert.Contains("open turn", TextOf(result));
    }

    [Fact]
    public async Task Present_TooManyFiles_FailsWithoutRecording()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        var schema = environment.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Schemas().Single(tool => tool.Name == ToolName);
        Assert.Contains($"at most {PresentTool.MaxFiles} per call", schema.Parameters["properties"]!["files"]!["description"]!.GetValue<string>());
        var files = new List<JsonObject>();
        for (var index = 0; index <= PresentTool.MaxFiles; index++)
        {
            await CreateFile(session, $"part-{index}.txt");
            files.Add(File($"part-{index}.txt"));
        }
        session.Append(new TurnStartPayload(1));

        var result = await Present(environment, [.. files]);

        Assert.True(result.IsError);
        Assert.Contains($"1 to {PresentTool.MaxFiles} files", TextOf(result));
        Assert.DoesNotContain(session.SnapshotEvents(), IsPresented);
    }

    [Fact]
    public async Task Present_BlockedByPostHook_RecordsNothing()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        await CreateFile(session, "slides.pptx");
        session.Append(new TurnStartPayload(1));
        var unhook = environment.App.Ctx.OnWaterfall<ToolPostExecuteNotification>(
            (_, _) => ValueTask.FromResult<object?>(new PostToolDecision.Block([new TextBlock("blocked by hook")])),
            new EventOptions { Global = true });

        var result = await Present(environment, File("slides.pptx"));
        unhook();

        Assert.True(result.IsError);
        Assert.DoesNotContain(session.SnapshotEvents(), IsPresented);
    }

    [Fact]
    public async Task Present_SamePathTwice_KeepsOneEntryWithLatestDescription()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        var report = await CreateFile(session, "report.docx");
        var data = await CreateFile(session, "data.xlsx");
        session.Append(new TurnStartPayload(1));

        await Present(environment, File("report.docx", "初稿"), File("data.xlsx"));
        await Present(environment, File(report, "终稿"));

        Assert.Equal(
            [new PresentedFile(report, "终稿"), new PresentedFile(data)],
            SessionDeliverables.Collect(session.SnapshotEvents()));
    }

    [Fact]
    public async Task Deliverables_FollowSessionTruncation()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        var first = await CreateFile(session, "first.docx");
        await CreateFile(session, "second.docx");
        session.Append(new TurnStartPayload(1));
        await Present(environment, File("first.docx"));
        var cut = session.Seq;
        await Present(environment, File("second.docx"));

        Assert.True(session.TryTruncateTo(cut));

        Assert.Equal([new PresentedFile(first)], SessionDeliverables.Collect(session.SnapshotEvents()));
    }

    [Fact]
    public async Task Deliverables_SurviveRestartAndResume_AndListThroughCommand()
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync();
        var session = environment.Agent.Session;
        var sessionId = session.Id;
        var expected = await CreateFile(session, "budget.xlsx");
        session.Append(new TurnStartPayload(1));
        await Present(environment, File("budget.xlsx", "预算表"));
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(session);

        await using var restarted = await environment.RestartAsync();
        var agents = restarted.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Resume(
            new ResumeAgentOptions(sessionId, new AgentOptions("test", "test-model")),
            TestContext.Current.CancellationToken);

        Assert.Equal([new PresentedFile(expected, "预算表")], SessionDeliverables.Collect(handle.Agent.Session.SnapshotEvents()));
        var commands = restarted.App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;
        var listed = await commands.Execute(handle.Agent, "/deliverables", TestContext.Current.CancellationToken);
        var text = Assert.IsType<CommandResult.Success>(listed?.Result).Text;
        Assert.Contains($"1. {expected} - 预算表", text);
    }

    [Fact]
    public async Task Deliverables_InheritedByFork()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var session = environment.Agent.Session;
        var expected = await CreateFile(session, "plan.docx");
        session.Append(new TurnStartPayload(1));
        await Present(environment, File("plan.docx", "计划"));
        await environment.App.Ctx.Get<SessionStore>(SessionStore.ServiceName)!.Flush(session);

        var persistence = environment.App.Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName)!;
        var forkId = SessionFork.Fork(persistence, session, session.Seq, "fork", session.Header.Cwd);
        var agents = environment.App.Ctx.Get<AgentRegistry>(AgentRegistry.ServiceName)!;
        var handle = await agents.Resume(
            new ResumeAgentOptions(forkId, new AgentOptions("test", "test-model")),
            TestContext.Current.CancellationToken);

        Assert.Equal([new PresentedFile(expected, "计划")], SessionDeliverables.Collect(handle.Agent.Session.SnapshotEvents()));
    }

    [Fact]
    public async Task DeliverablesCommand_UnknownIndex_ReportsError()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync();
        var commands = environment.App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;

        var listed = await commands.Execute(environment.Agent, "/deliverables", TestContext.Current.CancellationToken);
        var opened = await commands.Execute(environment.Agent, "/deliverables open 3", TestContext.Current.CancellationToken);

        Assert.Contains("no deliverables", Assert.IsType<CommandResult.Success>(listed?.Result).Text);
        Assert.Contains("no deliverable #3", Assert.IsType<CommandResult.Error>(opened?.Result).Text);
    }

    private static bool IsPresented(SessionEvent sessionEvent)
        => sessionEvent.Type == DeliverablesPresentedPayload.EventType;

    private static async Task<string> CreateFile(Session session, string name)
    {
        var path = Path.Combine(session.Header.Cwd!, name);
        await System.IO.File.WriteAllTextAsync(path, name, TestContext.Current.CancellationToken);
        return path;
    }

    private static JsonObject File(string path, string? description = null)
    {
        var file = new JsonObject { ["path"] = path };
        if (description is not null)
            file["description"] = description;
        return file;
    }

    private static Task<ToolExecutionResult> Present(GuiTestEnvironment environment, params JsonObject[] files)
    {
        var arguments = new JsonObject { ["files"] = new JsonArray([.. files]) };
        return environment.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = ToolName,
            Arguments = JsonDocument.Parse(arguments.ToJsonString()).RootElement,
            Agent = environment.Agent,
            Signal = TestContext.Current.CancellationToken,
        });
    }

    private static string TextOf(ToolExecutionResult result)
        => string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text));
}
