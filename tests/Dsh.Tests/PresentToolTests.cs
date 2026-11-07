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
    public async Task Present_ActualDeniedFile_IsNotPublished_AndKeepsTheOriginatingAudit()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
        var session = environment.Agent.Session;
        var path = await CreateFile(session, "private-delivery.txt");
        Dsh.Boot.HarnessSettings.Update(environment.App.Home, settings =>
        {
            (settings.Safety ??= new Dsh.Boot.SafetySettings()).Blacklist["deny-delivery-read"] = new() { Path = path, Intent = "read" };
            return true;
        });
        session.Append(new TurnStartPayload(1));
        var result = await Present(environment, File("private-delivery.txt"));
        Assert.True(result.IsError);
        Assert.DoesNotContain(session.SnapshotEvents(), IsPresented);
        Assert.Contains(session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.RuleId == "deny-delivery-read" && audit.Decision == "deny" && audit.Intent == "read"
            && audit.Path == Dsh.Runtime.CanonicalFilePath.Canonicalize(path) && audit.Tool == ToolName
            && audit.SessionId == session.Id.Value && !string.IsNullOrEmpty(audit.CallId));
    }

    private const string HomeSettings = """
        logging:
          file: false
        safety:
          autoApprove: false
          whitelist:
            presentation-consumer:
              tool: present
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Present_SecondTargetChangedAfterAcceptingPostHook_FailsWithoutPublishing(bool deny)
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync(HomeSettings);
        var session = environment.Agent.Session;
        var first = await CreateFile(session, "first-accepted.txt");
        var second = await CreateFile(session, "second-changed.txt");
        session.Append(new TurnStartPayload(1));
        var accepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var remove = environment.App.Ctx.OnWaterfall<ToolPostExecuteNotification>(async (notification, next) =>
        {
            var decision = await next();
            accepted.TrySetResult(true);
            await resume.Task.WaitAsync(notification.Run.Signal);
            return decision;
        }, new EventOptions { Global = true });
        try
        {
            var pending = Present(environment, File(first), File(second));
            await accepted.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (deny)
                Dsh.Boot.HarnessSettings.Update(environment.App.Home, settings =>
                {
                    (settings.Safety ??= new Dsh.Boot.SafetySettings()).Blacklist["deny-after-present-hook"] = new() { Path = second, Intent = "read" };
                    return true;
                });
            else
                System.IO.File.Delete(second);
            resume.TrySetResult(true);

            var result = await pending.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(result.IsError);
            Assert.DoesNotContain(session.SnapshotEvents(), IsPresented);
            Assert.Equal("first-accepted.txt", await System.IO.File.ReadAllTextAsync(first, TestContext.Current.CancellationToken));
            if (deny)
                Assert.Contains(session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
                    && audit.RuleId == "deny-after-present-hook" && audit.Decision == "deny" && audit.Intent == "read"
                    && audit.Path == Dsh.Runtime.CanonicalFilePath.Canonicalize(second) && audit.Tool == ToolName
                    && audit.SessionId == session.Id.Value && !string.IsNullOrEmpty(audit.CallId));
        }
        finally
        {
            resume.TrySetResult(true);
            remove();
        }
    }

    [Fact]
    public async Task Present_AfterCd_UsesTheEffectiveDirectoryForRelativeAndAbsoluteTargets()
    {
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync(HomeSettings);
        var session = environment.Agent.Session;
        var root = await CreateFile(session, "report.txt");
        var directory = Directory.CreateDirectory(Path.Combine(session.Header.Cwd!, "sub")).FullName;
        var nested = Path.Combine(directory, "report.txt");
        await System.IO.File.WriteAllTextAsync(root, "root-report-body", TestContext.Current.CancellationToken);
        await System.IO.File.WriteAllTextAsync(nested, "nested-report-body", TestContext.Current.CancellationToken);
        session.Append(new TurnStartPayload(1));
        using (ApprovalAnswerers.AutoApprove(environment.App.Ctx))
        {
            var changed = await environment.App.Ctx.Get<ToolRuntime>(ToolRuntime.ServiceName)!.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create(Guid.NewGuid().ToString("N")), Name = Dsh.Tools.WorkingDirectoryTool.ToolName,
                Arguments = JsonSerializer.SerializeToElement(new { cd = "sub" }), Agent = environment.Agent,
                Signal = TestContext.Current.CancellationToken,
            });
            Assert.False(changed.IsError, TextOf(changed));
        }
        Assert.Equal(directory, session.WorkingDirectory);
        Dsh.Boot.HarnessSettings.Update(environment.App.Home, settings =>
        {
            (settings.Safety ??= new Dsh.Boot.SafetySettings()).Blacklist["deny-original-report"] = new() { Path = root, Intent = "read" };
            return true;
        });

        var absolute = await Present(environment, File(nested, "absolute nested report"));
        var relative = await Present(environment, File("report.txt", "relative nested report"));

        Assert.False(absolute.IsError, TextOf(absolute));
        Assert.False(relative.IsError, TextOf(relative));
        var delivered = Assert.Single(SessionDeliverables.Collect(session.SnapshotEvents()));
        Assert.Equal(nested, delivered.Path);
        Assert.Equal("nested-report-body", await System.IO.File.ReadAllTextAsync(delivered.Path, TestContext.Current.CancellationToken));
        Assert.Contains(session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.Tool == ToolName && audit.Intent == "read" && audit.Path == Dsh.Runtime.CanonicalFilePath.Canonicalize(nested)
            && audit.SessionId == session.Id.Value && !string.IsNullOrEmpty(audit.CallId));

        Dsh.Boot.HarnessSettings.Update(environment.App.Home, settings =>
        {
            settings.Safety!.Blacklist.Clear();
            settings.Safety.Blacklist["deny-effective-report"] = new() { Path = nested, Intent = "read" };
            return true;
        });
        var originalAbsolute = await Present(environment, File(root, "absolute original report"));
        Assert.False(originalAbsolute.IsError, TextOf(originalAbsolute));
        var beforeDenied = session.SnapshotEvents().Where(IsPresented).ToArray();
        var denied = await Present(environment, File("report.txt"));
        Assert.True(denied.IsError);
        Assert.Equal(beforeDenied, session.SnapshotEvents().Where(IsPresented).ToArray());
        Assert.Contains(session.SnapshotEvents(), item => item.Data is SafetyDecisionAuditPayload audit
            && audit.RuleId == "deny-effective-report" && audit.Decision == "deny" && audit.Intent == "read"
            && audit.Path == Dsh.Runtime.CanonicalFilePath.Canonicalize(nested) && audit.Tool == ToolName
            && audit.SessionId == session.Id.Value && !string.IsNullOrEmpty(audit.CallId));
    }

    [Fact]
    public async Task Present_ExistingFile_RecordsAbsolutePathAndRendersIt()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
        var session = environment.Agent.Session;
        var expected = await CreateFile(session, "report.docx");
        session.Append(new TurnStartPayload(1));

        var result = await Present(environment, File("report.docx", "季度报告"));

        Assert.False(result.IsError, TextOf(result));
        Assert.Contains(expected, TextOf(result));
        Assert.Equal([new PresentedFile(expected, "季度报告")], SessionDeliverables.Collect(session.SnapshotEvents()));
        var presented = Assert.IsType<DeliverablesPresentedPayload>(session.SnapshotEvents().Last(IsPresented).Data);
        Assert.Equal(1, presented.Turn);
    }

    [Theory]
    [InlineData("missing.xlsx", false)]
    [InlineData("out", true)]
    public async Task Present_NonFileTarget_FailsWithoutRecording(string path, bool directory)
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
        var session = environment.Agent.Session;
        if (directory)
            Directory.CreateDirectory(Path.Combine(session.Header.Cwd!, path));
        session.Append(new TurnStartPayload(1));

        var result = await Present(environment, File(path));

        Assert.True(result.IsError);
        Assert.DoesNotContain(session.SnapshotEvents(), IsPresented);
    }

    [Fact]
    public async Task Present_OutsideOpenTurn_Fails()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
        await CreateFile(environment.Agent.Session, "notes.md");

        var result = await Present(environment, File("notes.md"));

        Assert.True(result.IsError);
        Assert.DoesNotContain(environment.Agent.Session.SnapshotEvents(), IsPresented);
    }

    [Fact]
    public async Task Present_TooManyFiles_FailsWithoutRecording()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
        var session = environment.Agent.Session;
        var files = new List<JsonObject>();
        for (var index = 0; index <= PresentTool.MaxFiles; index++)
        {
            await CreateFile(session, $"part-{index}.txt");
            files.Add(File($"part-{index}.txt"));
        }
        session.Append(new TurnStartPayload(1));

        var result = await Present(environment, [.. files]);

        Assert.True(result.IsError);
        Assert.DoesNotContain(session.SnapshotEvents(), IsPresented);
    }

    [Fact]
    public async Task Present_BlockedByPostHook_RecordsNothing()
    {
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
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
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
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
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
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
        await using var environment = await GuiTestEnvironment.CreateIsolatedAsync(HomeSettings);
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
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
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
        await using var environment = await GuiTestEnvironment.CreateAsync(HomeSettings);
        var commands = environment.App.Ctx.Get<CommandsService>(CommandsService.ServiceName)!;

        var listed = await commands.Execute(environment.Agent, "/deliverables", TestContext.Current.CancellationToken);
        var opened = await commands.Execute(environment.Agent, "/deliverables open 3", TestContext.Current.CancellationToken);

        Assert.Contains("no deliverables", Assert.IsType<CommandResult.Success>(listed?.Result).Text);
        Assert.Contains("no deliverable #3", Assert.IsType<CommandResult.Error>(opened?.Result).Text);
    }

    [Fact]
    public async Task Present_ReplacedAfterPreparation_PublishesExactlyOnceFromTheExecutedRegistration()
    {
        var root = TempTree.CreateDirectory("present-registration");
        try
        {
            var context = new Context();
            _ = new SystemPrompt(context, new SystemPromptConfig());
            _ = new LlmRuntime(context);
            var tools = new ToolRuntime(context);
            var projections = new SessionProjectionRegistry(context);
            projections.Register(TurnBoundaryProjectionDefinition.Instance);
            _ = new Dsh.Boot.SafetyPolicySource(context, () => new Dsh.Boot.HarnessSettings
            {
                Safety = new() { Whitelist = new() { ["present-call"] = new() { Tool = [ToolName] } } },
            });
            var id = SessionId.Create(Guid.NewGuid().ToString("N"));
            var session = Session.Create(id, null, new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion, Id = id, Cwd = root, CreatedAt = 0, IsSeeded = false,
            });
            session.Append(new TurnStartPayload(1));
            using var agent = new AgentLoopAgent(context, id, new AgentOptions(), session, _ => 0);
            var path = await CreateFile(session, "replacement-report.txt");
            using var original = PresentTool.Register(context);
            var callId = ToolCallId.Create(Guid.NewGuid().ToString("N"));
            var prepared = Assert.IsType<ScheduledToolPreparation.Dispatch>(await tools.PrepareScheduledExecution(new ToolExecutionInput
            {
                CallId = callId, Name = ToolName,
                Arguments = JsonSerializer.SerializeToElement(new { files = new[] { new { path, description = "Approved replacement report" } } }),
                Agent = agent, Signal = TestContext.Current.CancellationToken,
            }));
            original.Dispose();
            using var replacement = PresentTool.Register(context);
            var dispatched = Assert.IsType<ScheduledToolDispatch.PostResult>(await tools.DispatchScheduledExecution(prepared.Exec));
            var result = await tools.FinalizeScheduledExecution(prepared.Exec, dispatched.Result);
            Assert.False(result.IsError, TextOf(result));
            Assert.Single(session.SnapshotEvents().Select(item => item.Data).OfType<DeliverablesPresentedPayload>());
            Assert.Equal([new PresentedFile(path, "Approved replacement report")], SessionDeliverables.Collect(session.SnapshotEvents()));
        }
        finally { TempTree.Delete(root); }
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
