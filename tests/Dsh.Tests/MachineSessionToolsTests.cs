using System.Text.Json;
using Dsh.Core;
using Dsh.Gui.Shared.Mobile;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Tests;

/** 跨机器工具: 手机 agent 靠它看桌面上的会话、并往那边发消息。 */
public sealed class MachineSessionToolsTests
{
    [Fact]
    public async Task ListsEveryMachineWithItsSessions()
    {
        var (ctx, tools) = Harness(new FakeLink("桌面机", Session("s-1", "修表格")), new FakeLink("工作站"));

        var answer = await Execute(ctx, tools, MachineSessionTools.SessionsTool, "{}");

        Assert.True(answer.Ok);
        Assert.Contains("桌面机 · 已连接 · 1 个会话", answer.Text, StringComparison.Ordinal);
        Assert.Contains("s-1 · 修表格 · idle", answer.Text, StringComparison.Ordinal);
        Assert.Contains("工作站 · 已连接 · 没有会话", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAMachineThatIsNotConnected()
    {
        var (ctx, tools) = Harness(new FakeLink("桌面机") { IsConnected = false, Detail = "ssh 认证失败" });

        var answer = await Execute(ctx, tools, MachineSessionTools.SessionsTool, """{"machine":"桌面机"}""");

        Assert.True(answer.Ok);
        Assert.Equal("桌面机 · 未连接 · ssh 认证失败", answer.Text);
    }

    [Fact]
    public async Task SendsIntoTheGivenSession()
    {
        var link = new FakeLink("桌面机", Session("s-1", "修表格"));
        var (ctx, tools) = Harness(link);

        var answer = await Execute(ctx, tools, MachineSessionTools.SendTool,
            """{"machine":"桌面机","session":"s-1","text":"把表格补完"}""");

        Assert.True(answer.Ok);
        Assert.Equal("已发送到 桌面机/s-1", answer.Text);
        Assert.Equal(("s-1", "把表格补完"), Assert.Single(link.Sent));
        Assert.Empty(link.Created);
    }

    [Fact]
    public async Task CreatesASessionWhenNoneIsNamed()
    {
        var link = new FakeLink("桌面机");
        var (ctx, tools) = Harness(link);

        var answer = await Execute(ctx, tools, MachineSessionTools.SendTool,
            """{"machine":"桌面机","text":"先开个会话再发","cwd":"D:/work"}""");

        Assert.True(answer.Ok);
        Assert.Equal("已发送到 桌面机/created-1", answer.Text);
        Assert.Equal(["D:/work"], link.Created);
        Assert.Equal(("created-1", "先开个会话再发"), Assert.Single(link.Sent));
    }

    [Fact]
    public async Task UnknownMachineTellsTheModelHowToLook()
    {
        var (ctx, tools) = Harness(new FakeLink("桌面机"));

        var answer = await Execute(ctx, tools, MachineSessionTools.SendTool, """{"machine":"工作站","text":"hi"}""");

        Assert.False(answer.Ok);
        Assert.Contains("没有这台机器: 工作站", answer.Text, StringComparison.Ordinal);
        Assert.Contains(MachineSessionTools.SessionsTool, answer.Text, StringComparison.Ordinal);
    }

    private static (Context Ctx, ToolRuntime Tools) Harness(params FakeLink[] links)
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        _ = MachineSessionTools.Register(ctx, () => links);
        return (ctx, tools);
    }

    private static (bool Ok, string Text) Answer(ToolExecutionResult result)
    {
        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        return (success.Value.GetProperty("ok").GetBoolean(), success.Value.GetProperty("text").GetString() ?? "");
    }

    private static async Task<(bool Ok, string Text)> Execute(Context ctx, ToolRuntime tools, string name, string argumentsJson)
    {
        _ = ctx;
        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonDocument.Parse(argumentsJson).RootElement,
            Signal = default,
        });
        return Answer(result);
    }

    private static RemoteSessionSummary Session(string id, string title)
        => new()
        {
            Header = new SessionHeader { Version = SessionHeader.SessionFormatVersion, Id = SessionId.Create(id), CreatedAt = 0, Title = title, IsSeeded = false },
            Status = "idle",
        };

    private sealed class FakeLink(string name, params RemoteSessionSummary[] sessions) : IAgentMachineLink
    {
        private readonly List<RemoteSessionSummary> _sessions = [.. sessions];

        public string Name => name;
        public bool IsConnected { get; set; } = true;
        public string? Detail { get; set; }
        public IReadOnlyList<RemoteSessionSummary> Sessions => _sessions;
        public List<string> Created { get; } = [];
        public List<(string Session, string Text)> Sent { get; } = [];

        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
        {
            Created.Add(cwd);
            var created = Session("created-1", "新会话");
            _sessions.Add(created);
            return Task.FromResult(created);
        }

        public Task SendMessageAsync(string sessionId, string text, CancellationToken cancellationToken)
        {
            Sent.Add((sessionId, text));
            return Task.CompletedTask;
        }
    }
}
