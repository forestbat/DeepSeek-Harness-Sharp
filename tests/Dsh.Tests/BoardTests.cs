using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Runtime;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Board;

namespace Dsh.Tests;

public class BoardTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly IDisposable _tools;

        public Fixture()
        {
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            Prompts = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            Loop = new AgentLoop(Ctx);
            Board = SwarmBoard.Register(Ctx);
            _tools = BoardTools.Apply(Ctx);
            Tools.Register(new ToolDefinition
            {
                Name = "echo",
                Description = "echo tool",
                Parameters = new JsonObject(),
                Output = new ToolOutputDefinition(new JsonObject(), (_, value) => [new TextBlock(value.ToString())]),
                IsConcurrencySafe = _ => true,
                Execute = (_, _) => Task.FromResult<object?>(new { echo = true }),
            });
        }

        public Context Ctx { get; }
        public SessionStore Sessions { get; }
        public SystemPrompt Prompts { get; }
        public ToolRuntime Tools { get; }
        public AgentRegistry Agents { get; }
        public AgentLoop Loop { get; }
        public SwarmBoard Board { get; }

        public async Task<AgentLoopAgent> CreateMain(string id)
        {
            var handle = await Agents.Create(new CreateAgentOptions(
                SessionId.Create(id), null, new AgentOptions("test-provider", "test-model")));
            return (AgentLoopAgent)handle.Agent;
        }

        public AgentLoopAgent CreateChild(IAgent parent, string id)
        {
            var childId = SessionId.Create(id);
            var session = Sessions.Create(childId, header: new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = childId,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ParentSession = parent.Id,
                IsSeeded = false,
                Origin = "subagent",
                DelegationDepth = 1,
                RootSession = parent.Id,
                SubagentProvider = "spawn",
                SubagentMode = "one-shot",
            });
            return new AgentLoopAgent(Ctx.Root, childId, new AgentOptions("test-provider", "test-model"), session, _ => 0);
        }

        public void Dispose() => _tools.Dispose();
    }

    private static Task<JsonElement> Post(Fixture fixture, IAgent agent, string callId, string to, string type, string body, string? replyTo = null)
    {
        var args = new JsonObject
        {
            ["to"] = to,
            ["type"] = type,
            ["body"] = body,
        };
        if (replyTo is not null)
            args["reply_to"] = replyTo;
        return Execute(fixture, agent, callId, BoardTools.PostToolName, args);
    }

    private static async Task<JsonElement> Execute(Fixture fixture, IAgent agent, string callId, string tool, JsonObject args)
    {
        var result = await ExecuteRaw(fixture, agent, callId, tool, args);
        var failure = result as ToolExecutionResult.Failure;
        Assert.Null(failure);
        return Assert.IsType<ToolExecutionResult.Success>(result).Value;
    }

    private static async Task<ToolExecutionResult> ExecuteRaw(Fixture fixture, IAgent agent, string callId, string tool, JsonObject args)
        => await fixture.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create(callId),
            Name = tool,
            Arguments = JsonDocument.Parse(args.ToJsonString()).RootElement,
            Agent = agent,
            Signal = default,
        });

    [Fact]
    public async Task PostAndRead_IncrementallyAcrossTree()
    {
        using var fixture = new Fixture();
        var main = await fixture.CreateMain("session-board-main-1");
        var child = fixture.CreateChild(main, "session-board-child-1");

        var posted = await Post(fixture, child, "call-post-1", "main", "INFO", "found the root cause");
        Assert.Equal(1, posted!.GetProperty("seq").GetInt64());

        var read = await Execute(fixture, main, "call-read-1", BoardTools.ReadToolName, new JsonObject());
        var page = read!;
        Assert.Equal(1, page.GetProperty("cursor").GetInt64());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        var message = Assert.Single(page.GetProperty("messages").EnumerateArray());
        Assert.Equal(child.Id.Value, message.GetProperty("from").GetString());
        Assert.Equal("main", message.GetProperty("to").GetString());
        Assert.Equal("INFO", message.GetProperty("type").GetString());
        Assert.Equal("found the root cause", message.GetProperty("body").GetString());
        var participants = page.GetProperty("participants").EnumerateArray().Select(row => row.GetString()).ToList();
        Assert.Contains("main", participants);
        Assert.Contains(child.Id.Value, participants);

        var second = await Execute(fixture, main, "call-read-2", BoardTools.ReadToolName,
            new JsonObject { ["since"] = page.GetProperty("cursor").GetInt64() });
        Assert.Empty(second!.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task Post_IsIdempotentByToolCallId()
    {
        using var fixture = new Fixture();
        var main = await fixture.CreateMain("session-board-main-2");
        var child = fixture.CreateChild(main, "session-board-child-2");

        var first = await Post(fixture, child, "call-post-same", "ALL", "RESULT", "candidate answer");
        var retry = await Post(fixture, child, "call-post-same", "ALL", "RESULT", "candidate answer");
        Assert.Equal(first!.GetProperty("id").GetString(), retry!.GetProperty("id").GetString());

        var read = await Execute(fixture, main, "call-read-3", BoardTools.ReadToolName, new JsonObject());
        Assert.Single(read!.GetProperty("messages").EnumerateArray());

        var conflict = await ExecuteRaw(fixture, child, "call-post-same", BoardTools.PostToolName,
            new JsonObject { ["to"] = "ALL", ["type"] = "INFO", ["body"] = "different body" });
        var failure = Assert.IsType<ToolExecutionResult.Failure>(conflict);
        Assert.Equal(BoardErrorCodes.Conflict, failure.Error.Info?.Code);
    }

    [Fact]
    public async Task Post_EnforcesMessageSizeCap()
    {
        using var fixture = new Fixture();
        var main = await fixture.CreateMain("session-board-main-3");
        var oversized = new string('x', SwarmBoard.MaxMessageBytes + 1);

        var result = await ExecuteRaw(fixture, main, "call-post-big", BoardTools.PostToolName,
            new JsonObject { ["to"] = "ALL", ["type"] = "INFO", ["body"] = oversized });

        var failure = Assert.IsType<ToolExecutionResult.Failure>(result);
        Assert.Equal(BoardErrorCodes.CapacityExceeded, failure.Error.Info?.Code);
    }

    [Fact]
    public async Task Notifier_PiggybacksOnUnrelatedToolResults()
    {
        using var fixture = new Fixture();
        var main = await fixture.CreateMain("session-board-main-4");
        var child = fixture.CreateChild(main, "session-board-child-4");
        await Post(fixture, child, "call-post-4", "main", "HOLD", "do not touch the parser yet");

        var noticed = await ExecuteRaw(fixture, main, "call-echo-1", "echo", new JsonObject());
        var success = Assert.IsType<ToolExecutionResult.Success>(noticed);
        Assert.Contains(success.Content.OfType<TextBlock>(), block => block.Text.Contains("shared-board-notice"));

        var quiet = await ExecuteRaw(fixture, main, "call-echo-2", "echo", new JsonObject());
        var quietSuccess = Assert.IsType<ToolExecutionResult.Success>(quiet);
        Assert.DoesNotContain(quietSuccess.Content.OfType<TextBlock>(), block => block.Text.Contains("shared-board-notice"));
    }

    [Fact]
    public async Task Activity_AddressingRespectsRecipient()
    {
        using var fixture = new Fixture();
        var main = await fixture.CreateMain("session-board-main-5");
        var childA = fixture.CreateChild(main, "session-board-child-5a");
        var childB = fixture.CreateChild(main, "session-board-child-5b");

        await Post(fixture, childA, "call-post-5a", childB.Id.Value, "ASK", "can you take the parser?");
        Assert.Equal(0, fixture.Board.Activity(main, 0));
        Assert.Equal(1, fixture.Board.Activity(childB, 0));
        Assert.Equal(0, fixture.Board.Activity(childA, 0));

        await Post(fixture, childA, "call-post-5b", "ALL", "INFO", "broadcast finding");
        Assert.Equal(2, fixture.Board.Activity(main, 0));
        Assert.Equal(2, fixture.Board.Activity(childB, 0));
        Assert.Equal(0, fixture.Board.Activity(childA, 0));
    }

    [Fact]
    public async Task Claim_ReportsOverlap_And_ReleaseFrees()
    {
        using var fixture = new Fixture();
        var main = await fixture.CreateMain("session-board-claim-main");
        var child = fixture.CreateChild(main, "session-board-claim-child");

        var first = await Claim(fixture, main, "call-claim-1", "src/a.cs", 10, 20, "edit a");
        Assert.Equal(0, first.GetProperty("conflicts").GetArrayLength());
        var claimId = first.GetProperty("claim_id").GetString();
        Assert.False(string.IsNullOrEmpty(claimId));

        var second = await Claim(fixture, child, "call-claim-2", "src/a.cs", 15, 25, "edit a too");
        Assert.Equal(1, second.GetProperty("conflicts").GetArrayLength());
        Assert.Equal("main", second.GetProperty("conflicts")[0].GetProperty("owner").GetString());

        var read = await Execute(fixture, child, "call-read-claim", BoardTools.ReadToolName, new JsonObject());
        Assert.Equal(2, read.GetProperty("claims").GetArrayLength());

        var released = await Execute(fixture, main, "call-release-1", BoardTools.ReleaseToolName,
            new JsonObject { ["claim_id"] = claimId });
        Assert.Equal(1, released.GetProperty("released").GetInt32());
        var third = await Claim(fixture, child, "call-claim-3", "src/a.cs", 10, 20, "retry");
        Assert.Equal(0, third.GetProperty("conflicts").GetArrayLength());
    }

    [Fact]
    public async Task Claim_ExpiresAfterTtl()
    {
        using var fixture = new Fixture();
        var main = await fixture.CreateMain("session-board-ttl-main");
        await Execute(fixture, main, "call-claim-ttl", BoardTools.ClaimToolName, new JsonObject
        {
            ["path"] = "src/b.cs",
            ["start_line"] = 1,
            ["end_line"] = 2,
            ["intent"] = "x",
            ["ttl"] = 1,
        });
        var before = await Execute(fixture, main, "call-read-before", BoardTools.ReadToolName, new JsonObject());
        Assert.Equal(1, before.GetProperty("claims").GetArrayLength());

        await Task.Delay(1100, TestContext.Current.CancellationToken);

        var after = await Execute(fixture, main, "call-read-after", BoardTools.ReadToolName, new JsonObject());
        Assert.Equal(0, after.GetProperty("claims").GetArrayLength());
    }

    private static Task<JsonElement> Claim(Fixture fixture, IAgent agent, string callId, string path, int start, int end, string intent)
        => Execute(fixture, agent, callId, BoardTools.ClaimToolName, new JsonObject
        {
            ["path"] = path,
            ["start_line"] = start,
            ["end_line"] = end,
            ["intent"] = intent,
        });
}
