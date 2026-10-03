using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Toon;
using Dsh.Tui;

namespace Dsh.Tests;

/** 分屏协作读取工具: pane_list/pane_read/session_read 的内容与格式面(text/json/toon)。 */
public sealed class PaneReadToolsTests : IDisposable
{
    private readonly string _homeDir = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/test-homes")),
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PaneList_Lists_All_Panes_With_Kind_And_Session()
    {
        var (chat, tools, first, second) = await CreateChatWithTools();

        var result = await RunTool(chat, tools, PaneReadTools.PaneListToolName, "{}");

        var rows = SuccessValue(result).EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("chat", row.GetProperty("kind").GetString()));
        Assert.Equal(second.Id.ToString(), rows[1].GetProperty("sessionId").GetString());
        Assert.False(rows[0].GetProperty("focused").GetBoolean());
        Assert.True(rows[1].GetProperty("focused").GetBoolean());
        Assert.Equal(first.Id.ToString(), rows[0].GetProperty("sessionId").GetString());
        chat.Dispose();
    }

    [Fact]
    public async Task PaneRead_Returns_Transcript_Tail()
    {
        var (chat, tools, _, second) = await CreateChatWithTools();
        second.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "窗格内容标记")));
        chat.DrainUi();

        var result = await RunTool(chat, tools, PaneReadTools.PaneReadToolName, """{"paneId": 1}""");

        var value = SuccessValue(result);
        Assert.Equal("chat", value.GetProperty("kind").GetString());
        Assert.Contains("窗格内容标记", string.Join('\n', value.GetProperty("lines").EnumerateArray().Select(line => line.GetString())));
        chat.Dispose();
    }

    [Fact]
    public async Task PaneRead_Truncates_To_Requested_Lines()
    {
        var (chat, tools, _, second) = await CreateChatWithTools();
        for (var index = 0; index < 5; index++)
            second.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, $"line-{index}\n")));
        chat.DrainUi();

        var result = await RunTool(chat, tools, PaneReadTools.PaneReadToolName, """{"paneId": 1, "lines": 2}""");

        var value = SuccessValue(result);
        Assert.True(value.GetProperty("truncated").GetBoolean());
        var lines = value.GetProperty("lines").EnumerateArray().Select(line => line.GetString()).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Contains("line-4", lines[1]);
        chat.Dispose();
    }

    [Fact]
    public async Task PaneRead_Unknown_Pane_Fails()
    {
        var (chat, tools, _, _) = await CreateChatWithTools();

        var result = await RunTool(chat, tools, PaneReadTools.PaneReadToolName, """{"paneId": 999}""");

        Assert.True(result.IsError);
        chat.Dispose();
    }

    [Fact]
    public async Task SessionRead_Reads_Session_Transcript()
    {
        var (chat, tools, first, _) = await CreateChatWithTools();
        first.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "会话内容标记")));

        var result = await RunTool(chat, tools, PaneReadTools.SessionReadToolName,
            $$"""{"sessionId": "{{first.Id}}"}""");

        var value = SuccessValue(result);
        Assert.Equal(first.Id.ToString(), value.GetProperty("sessionId").GetString());
        Assert.Contains("会话内容标记", string.Join('\n', value.GetProperty("lines").EnumerateArray().Select(line => line.GetString())));
        chat.Dispose();
    }

    [Fact]
    public async Task SessionRead_ToonFormat_RoundTrips_Through_ToonCodec()
    {
        var (chat, tools, first, _) = await CreateChatWithTools(registerToonCodec: true);
        first.Session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, "toon格式标记")));

        var result = await RunTool(chat, tools, PaneReadTools.SessionReadToolName,
            $$"""{"sessionId": "{{first.Id}}", "format": "toon"}""");

        var toon = SuccessText(result);
        var decoded = ToonCodec.Decode(toon)!.AsObject();
        Assert.Equal(first.Id.ToString(), decoded["sessionId"]!.GetValue<string>());
        Assert.Contains(decoded["lines"]!.AsArray(), line => line!.GetValue<string>().Contains("toon格式标记"));
        chat.Dispose();
    }

    [Fact]
    public async Task ToonFormat_Without_Toon_Plugin_Fails_With_Clear_Error()
    {
        var (chat, tools, first, _) = await CreateChatWithTools();

        var result = await RunTool(chat, tools, PaneReadTools.SessionReadToolName,
            $$"""{"sessionId": "{{first.Id}}", "format": "toon"}""");

        Assert.True(result.IsError);
        Assert.Contains("@deepseek-ai/dsh-toon", result is ToolExecutionResult.Failure failure ? failure.Error.Message : "");
        chat.Dispose();
    }

    private async Task<(ChatWindow Chat, ToolRuntime Tools, AgentLoopAgent First, AgentLoopAgent Second)> CreateChatWithTools(
        bool registerToonCodec = false)
    {
        var ctx = new Context();
        _ = new SessionStore(ctx);
        var agents = new AgentRegistry(ctx);
        _ = new AgentLoop(ctx);
        CommandsService.Register(ctx);
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        if (registerToonCodec)
            ctx.Provide(IToonCodec.ServiceName, new ToonCodecAdapter());
        var first = await CreateAgent(agents);
        var second = await CreateAgent(agents);
        var chat = new ChatWindow(ctx, first, HarnessHome.Resolve(_homeDir));
        chat.AddPane(second, SplitOrientation.Vertical);
        chat.RegisterPaneTools();
        return (chat, tools, first, second);
    }

    private static async Task<AgentLoopAgent> CreateAgent(AgentRegistry agents)
    {
        var handle = await agents.Create(new CreateAgentOptions(
            SessionId.Create($"session-{Guid.NewGuid():N}"),
            null,
            new AgentOptions("deepseek-official", "deepseek-v4-flash")));
        var agent = (AgentLoopAgent)handle.Agent;
        await agent.WhenIdle();
        return agent;
    }

    /** 工具的窗格读取经 QueueAction 编排到 UI 线程: 循环 DrainUi 直到执行完成。 */
    private static async Task<ToolExecutionResult> RunTool(ChatWindow chat, ToolRuntime tools, string name, string arguments)
    {
        var document = JsonDocument.Parse(arguments);
        var task = tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"test-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = document.RootElement,
            RawArguments = arguments,
            Signal = TestContext.Current.CancellationToken,
        });
        for (var attempt = 0; attempt < 500 && !task.IsCompleted; attempt++)
        {
            chat.DrainUi();
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        return await task;
    }

    private static JsonElement SuccessValue(ToolExecutionResult result)
    {
        Assert.False(result.IsError, result is ToolExecutionResult.Failure failure ? failure.Error.Message : null);
        return ((ToolExecutionResult.Success)result).Value;
    }

    private static string SuccessText(ToolExecutionResult result)
    {
        Assert.False(result.IsError, result is ToolExecutionResult.Failure failure ? failure.Error.Message : null);
        return string.Join('\n', result.Content.OfType<TextBlock>().Select(block => block.Text));
    }

    /** 组装侧适配: 契约接口接到真实 ToonCodec 上(与 Dsh.Toon 插件 Apply 时的注册同形)。 */
    private sealed class ToonCodecAdapter : IToonCodec
    {
        public string Encode(JsonNode? value) => ToonCodec.Encode(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_homeDir))
            Directory.Delete(_homeDir, true);
    }
}
