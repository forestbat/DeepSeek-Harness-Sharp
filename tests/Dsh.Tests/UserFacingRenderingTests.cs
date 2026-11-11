using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Agent;

namespace Dsh.Tests;

/** 会话界面展示层的三个纯函数约定: 去掉只给模型看的注入块、工具参数还原转义、diff 只展开改动附近。 */
public sealed class UserFacingRenderingTests
{
    [Fact]
    public void StripSystemReminders_RemovesModelOnlyBlocks()
    {
        var text = "请看这里\n<system-reminder>\nAGENTS.md 内容\n</system-reminder>\n结论";

        Assert.Equal("请看这里\n\n结论", UserFacingText.StripSystemReminders(text));
    }

    [Fact]
    public void StripSystemReminders_DropsUnclosedTailBlock()
    {
        Assert.Equal("正文", UserFacingText.StripSystemReminders("正文\n<system-reminder>\n未闭合"));
    }

    [Fact]
    public void StripSystemReminders_KeepsTextWithoutBlocks()
    {
        const string Text = "普通消息 <b>不是注入</b>";

        Assert.Equal(Text, UserFacingText.StripSystemReminders(Text));
    }

    [Fact]
    public void FormatArguments_DecodesUnicodeEscapes()
    {
        var formatted = ToolPresentation.FormatArguments("""{"content":"\u6D4B\u8BD5","status":"completed"}""");

        Assert.Contains("测试", formatted);
        Assert.DoesNotContain("\\u6D4B", formatted);
    }

    [Fact]
    public void FormatArguments_ShowsShellCommandBeforeOtherParameters()
    {
        var formatted = ToolPresentation.FormatArguments("""{"command":"Get-Process","description":"List processes"}""");

        Assert.Equal("Get-Process\ndescription: List processes", formatted);
    }

    [Fact]
    public void FormatArguments_KeepsMalformedArgumentsReadable()
    {
        Assert.Equal("{not json", ToolPresentation.FormatArguments("{not json"));
    }

    [Fact]
    public void FormatResult_DecodesEscapedJsonResult()
    {
        var formatted = ToolPresentation.FormatResult("""{"items":[],"name":"\u4F1A\u8BDD\u5217\u8868"}""");

        Assert.Contains("会话列表", formatted);
    }

    [Fact]
    public void FormatResult_LeavesPlainTextAlone()
    {
        Assert.Equal("第一行\n第二行", ToolPresentation.FormatResult("第一行\n第二行"));
    }

    [Fact]
    public void DiffDisplay_KeepsThreeContextLinesAroundEachChange()
    {
        var lines = Enumerable.Range(0, 40)
            .Select(index => new DiffLine(index == 20 ? DiffLineKind.Add : DiffLineKind.Context, index + 1, index + 1, $"line {index}"))
            .ToList();

        var display = DiffCardDisplay.ForDisplay(lines);

        Assert.Equal(Enumerable.Range(17, 7).Select(index => $"line {index}"), display.Select(line => line.Text));
    }

    [Fact]
    public void DiffDisplay_SeparatesDistantChangesWithElision()
    {
        var lines = Enumerable.Range(0, 40)
            .Select(index => new DiffLine(index is 5 or 30 ? DiffLineKind.Delete : DiffLineKind.Context, index + 1, index + 1, $"line {index}"))
            .ToList();

        var display = DiffCardDisplay.ForDisplay(lines);

        Assert.Contains(display, line => line.Text == "…");
        Assert.Contains(display, line => line.Text == "line 5");
        Assert.Contains(display, line => line.Text == "line 30");
        Assert.DoesNotContain(display, line => line.Text == "line 15");
    }
}

/** 渐进式披露(默认关闭): 显式开启后成组工具默认不可见, 由路由工具展开才进入模型的工具清单。 */
public sealed class ToolDisclosureTests
{
    /** 默认关闭: 不分组、不出现路由工具, 模型直接看到全部工具(它不一定认识"路由工具"这种约定)。 */
    [Fact]
    public async Task ProgressiveDisclosure_IsOffByDefault_SoEveryToolIsVisible()
    {
        var ctx = new Context();
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });
        var tools = new ToolRuntime(ctx);
        using var grouped = tools.Register(Definition("mcp_server_lookup", new ToolDisclosure("mcp:rider", "MCP rider tools")));

        var names = await AssembleToolNames(prompt);

        Assert.Equal(["mcp_server_lookup"], names);
        Assert.DoesNotContain(ToolRuntime.RouterToolName, names);
    }

    [Fact]
    public async Task GroupedTools_StayHiddenUntilTheRouterOpensTheirGroup()
    {
        var ctx = new Context();
        ctx.SetOwn(ToolDisclosureSwitch.PropName, true);
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });
        var tools = new ToolRuntime(ctx);
        using var plain = tools.Register(Definition("plain_tool", null));
        using var grouped = tools.Register(Definition("mcp_server_lookup", new ToolDisclosure("mcp:rider", "MCP rider tools")));

        var before = await AssembleToolNames(prompt);
        Assert.Contains("plain_tool", before);
        Assert.Contains(ToolRuntime.RouterToolName, before);
        Assert.DoesNotContain("mcp_server_lookup", before);

        var listed = await ExecuteRouter(tools, """{}""");
        Assert.Contains("mcp:rider", listed);

        var opened = await ExecuteRouter(tools, """{"group":"mcp:rider"}""");
        Assert.Contains("mcp_server_lookup", opened);

        var after = await AssembleToolNames(prompt);
        Assert.Contains("mcp_server_lookup", after);
    }

    [Fact]
    public async Task Router_IsAbsentWhenNothingIsGrouped()
    {
        var ctx = new Context();
        ctx.SetOwn(ToolDisclosureSwitch.PropName, true);
        var prompt = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });
        var tools = new ToolRuntime(ctx);
        using var plain = tools.Register(Definition("plain_tool", null));

        var names = await AssembleToolNames(prompt);

        Assert.Equal(["plain_tool"], names);
    }

    [Fact]
    public void PlatformUnavailableTools_AreHiddenFromEverySchemaList()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });
        var tools = new ToolRuntime(ctx);
        using var visible = tools.Register(Definition("visible_tool", null));
        using var hidden = tools.Register(Definition("hidden_tool", null, available: false));

        var names = tools.Schemas().Select(schema => schema.Name).ToList();

        Assert.Contains("visible_tool", names);
        Assert.DoesNotContain("hidden_tool", names);
    }

    /**
     * 展开状态随会话落盘: 重启进程后同一会话里那批工具必须还在。
     * 否则上下文里已有该组的工具调用与结果, 而工具清单里却没有这些工具, 模型会看到断裂的对话。
     */
    [Fact]
    public async Task DisclosedGroup_SurvivesARestartOfTheSameSession()
    {
        var session = Session.Create(SessionId.Create($"session-{Guid.NewGuid():N}"));
        var first = new Context();
        first.SetOwn(ToolDisclosureSwitch.PropName, true);
        var firstPrompt = new SystemPrompt(first, new SystemPromptConfig { IncludeHarnessIdentity = false });
        var firstTools = new ToolRuntime(first);
        using var groupedInFirst = firstTools.Register(Definition("mcp_server_lookup", new ToolDisclosure("mcp:rider", "MCP rider tools")));
        var agent = new DisclosureAgent(first, session);

        await ExecuteRouter(firstTools, """{"group":"mcp:rider"}""", agent);
        Assert.Contains("mcp_server_lookup", await AssembleToolNames(firstPrompt, agent));

        // 新进程 = 新的 Context/ToolRuntime, 只有会话历史延续下来。
        var second = new Context();
        second.SetOwn(ToolDisclosureSwitch.PropName, true);
        var secondPrompt = new SystemPrompt(second, new SystemPromptConfig { IncludeHarnessIdentity = false });
        var secondTools = new ToolRuntime(second);
        using var groupedInSecond = secondTools.Register(Definition("mcp_server_lookup", new ToolDisclosure("mcp:rider", "MCP rider tools")));
        var restored = new DisclosureAgent(second, session);

        var names = await AssembleToolNames(secondPrompt, restored);

        Assert.Contains("mcp_server_lookup", names);
        Assert.DoesNotContain(ToolRuntime.RouterToolName, names);
    }

    [Fact]
    public async Task Router_ReportsUnknownGroupWithoutOpeningAnything()
    {
        var ctx = new Context();
        ctx.SetOwn(ToolDisclosureSwitch.PropName, true);
        _ = new SystemPrompt(ctx, new SystemPromptConfig { IncludeHarnessIdentity = false });
        var tools = new ToolRuntime(ctx);
        using var grouped = tools.Register(Definition("mcp_server_lookup", new ToolDisclosure("mcp:rider", "MCP rider tools")));

        var text = await ExecuteRouter(tools, """{"group":"mcp:missing"}""");

        Assert.Contains("no group matches", text);
        Assert.Empty(tools.GroupTools("mcp:missing", null));
    }

    private static ToolDefinition Definition(string name, ToolDisclosure? disclosure, bool available = true) => new()
    {
        Name = name,
        Description = $"{name} description",
        Parameters = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
        Output = new ToolOutputDefinition(
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } } },
            (_, value) => [new TextBlock(value.TryGetProperty("text", out var text) ? text.GetString() ?? "" : value.GetRawText())]),
        Execute = (_, _) => Task.FromResult<object?>(JsonSerializer.SerializeToElement(new { text = name })),
        Disclosure = disclosure,
        IsAvailable = () => available,
    };

    private static async Task<List<string>> AssembleToolNames(SystemPrompt prompt, IAgent? agent = null)
        => [.. (await prompt.Assemble(new AssembleContext { Agent = agent })).Tools.Select(tool => tool.Name)];

    private static async Task<string> ExecuteRouter(ToolRuntime tools, string arguments, IAgent? agent = null)
    {
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = ToolRuntime.RouterToolName,
            Arguments = JsonDocument.Parse(arguments).RootElement,
            Agent = agent,
            Signal = default,
        });
        Assert.False(result.IsError);
        return string.Join('\n', result.Content.OfType<TextBlock>().Select(block => block.Text));
    }

    /** 只要有会话的工具运行时; 展开状态靠 Session 上的事件跨进程传递。 */
    private sealed class DisclosureAgent(Context ctx, Session session) : IAgent
    {
        public SessionId Id { get; } = session.Id;
        public Session Session { get; } = session;
        public ScopeKey ScopeKey { get; } = new();
        public Context Ctx { get; } = ctx;
        public AgentStatus Status => AgentStatus.Idle;
        public AgentOptions Options { get; } = new();

        public void Cancel(AgentCancelCause cause, bool keepInbox = false) { }
        public Task WhenIdle() => Task.CompletedTask;
        public void Send(UserMessage message, string target, bool wakeup) { }
        public void Followup(UserMessage message) { }
        public void Steer(UserMessage message) { }
        public void Inject(UserMessage message) { }
    }
}
