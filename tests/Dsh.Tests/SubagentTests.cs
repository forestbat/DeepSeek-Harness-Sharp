using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cordis;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Subagent;

namespace Dsh.Tests;

public class SubagentTests
{
    private sealed class ScriptedAdapter : LlmAdapter
    {
        private readonly Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> _script;

        public ScriptedAdapter(string provider, IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script)
        {
            _script = new Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>>(script);
            ProviderInfo = new LlmProviderInfo(provider, provider);
        }

        public List<GenerateOptions> Requests { get; } = [];

        public override LlmProviderInfo ProviderInfo { get; }

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
        {
            Requests.Add(options);
            if (_script.Count == 0)
                throw new InvalidOperationException("scripted adapter: no scripted response left");
            return Yield(_script.Dequeue()(options), cancellationToken);
        }

        private static async IAsyncEnumerable<StreamChunk> Yield(
            IEnumerable<StreamChunk> chunks, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var chunk in chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.Yield();
            }
        }
    }

    private sealed class SubagentFixture : IDisposable
    {
        private readonly IDisposable _spawn;
        private readonly IDisposable _fork;
        private readonly IDisposable _tool;
        private readonly IDisposable _control;

        public SubagentFixture(
            IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script,
            SubagentToolConfig? toolConfig = null)
        {
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            var llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            _ = new AgentLoop(Ctx);
            Approval = ApprovalService.Register(Ctx);
            Subagents = new SubagentRuntime(Ctx);
            Adapter = new ScriptedAdapter("test-provider", script);
            llm.RegisterAdapter(["test-provider"], Adapter);
            _spawn = SubagentInProcessProviders.RegisterSpawn(Ctx);
            _fork = SubagentInProcessProviders.RegisterFork(Ctx);
            _tool = SubagentTool.Apply(Ctx, toolConfig ?? new SubagentToolConfig { Provider = "spawn" });
            _control = SubagentControlTools.ApplyListAgents(Ctx);
        }

        public Context Ctx { get; }
        public SessionStore Sessions { get; }
        public ToolRuntime Tools { get; }
        public ApprovalService Approval { get; }
        private AgentRegistry Agents { get; }
        public SubagentRuntime Subagents { get; }
        public ScriptedAdapter Adapter { get; }

        public async Task<AgentLoopAgent> CreateParent(string id)
        {
            var handle = await Agents.Create(new CreateAgentOptions(
                SessionId.Create(id), null, new AgentOptions("test-provider", "test-model")));
            return (AgentLoopAgent)handle.Agent;
        }

        public Session? ChildOf(SessionId parentId, int depth = 1)
            => Sessions.List().SingleOrDefault(session =>
                session.Header.ParentSession == parentId && session.Header.DelegationDepth == depth);

        public void Dispose()
        {
            _tool.Dispose();
            _control.Dispose();
            _spawn.Dispose();
            _fork.Dispose();
        }
    }

    private static IReadOnlyList<StreamChunk> TextAnswer(string text) =>
    [
        new StreamChunk.BlockStart(0, "text"),
        new StreamChunk.TextDelta(0, text),
        new StreamChunk.BlockEnd(0, new TextBlock(text)),
        new StreamChunk.Finish(new FinishReason.Stop()),
    ];

    private static IReadOnlyList<StreamChunk> ToolCallAnswer(string callId, string name, string arguments) =>
    [
        new StreamChunk.ToolCallDelta(0, ToolCallId.Create(callId), name, arguments),
        new StreamChunk.Finish(new FinishReason.ToolCalls()),
    ];

    private static List<T> CollectEvents<T>(Context ctx, string name)
    {
        var collected = new List<T>();
        ctx.On(name, (_, args) =>
        {
            if (args[0] is T payload)
                collected.Add(payload);
            return new ValueTask<object?>();
        }, new EventOptions { Global = true });
        return collected;
    }

    [Fact]
    public async Task SpawnDelegation_RunsChildAndReturnsFinalText()
    {
        using var fixture = new SubagentFixture(
        [
            _ => ToolCallAnswer("call-sub-1", "subagent", """{"description":"research task","prompt":"do the delegated thing"}"""),
            _ => TextAnswer("child final answer"),
            _ => TextAnswer("parent done"),
        ]);
        var starts = CollectEvents<SubagentRunInfo>(fixture.Ctx, SubagentRuntime.StartEvent);
        var ends = CollectEvents<SubagentRunEndInfo>(fixture.Ctx, SubagentRuntime.EndEvent);
        var parent = await fixture.CreateParent("session-parent-spawn");

        parent.Followup(MessageFactory.CreateUserText("start"));
        await parent.WhenIdle();

        var toolResult = parent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<ToolResultPayload>()
            .Single();
        Assert.False(toolResult.Message.Block.IsError);
        Assert.Equal("child final answer", string.Concat(toolResult.Message.Block.Content.OfType<TextBlock>().Select(block => block.Text)));

        var child = fixture.ChildOf(parent.Id);
        Assert.NotNull(child);
        Assert.Equal("subagent", child.Header.Origin);
        Assert.Equal(1, child.Header.DelegationDepth);
        Assert.Equal(parent.Id, child.Header.ParentSession);
        Assert.False(child.Header.IsSeeded);

        var descriptor = child.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<SubagentDescriptorPayload>()
            .Single();
        Assert.Equal(SubagentDescriptorPayload.CurrentVersion, descriptor.Version);
        Assert.Equal(SubagentDescriptorPayload.OneShotMode, descriptor.Mode);
        Assert.Equal("spawn", descriptor.Provider);
        Assert.Equal("research task", descriptor.Label);

        var start = Assert.Single(starts);
        Assert.Equal(child.Id, start.Id);
        Assert.Equal("spawn", start.Provider);
        Assert.True(start.Local);
        var end = Assert.Single(ends);
        Assert.Equal(start.RunId, end.RunId);
        Assert.Equal(SubagentStopReason.Completed, end.StopReason);
        Assert.Equal("child final answer", string.Concat(end.LastAssistantMessage!.OfType<TextBlock>().Select(block => block.Text)));

        Assert.Equal(3, fixture.Adapter.Requests.Count);
        Assert.Equal(child.Id, fixture.Adapter.Requests[1].SessionId);
        Assert.Contains(fixture.Adapter.Requests[1].Messages, message =>
            message.Content.OfType<TextBlock>().Any(block => block.Text == "do the delegated thing"));
    }

    [Fact]
    public async Task SpawnDelegation_PinsDelegatedApprovalPolicy()
    {
        using var fixture = new SubagentFixture(
        [
            _ => ToolCallAnswer("call-sub-1", "subagent", """{"description":"policy task","prompt":"check approval pin"}"""),
            _ => TextAnswer("done"),
            _ => TextAnswer("ok"),
        ]);
        var parent = await fixture.CreateParent("session-parent-policy");

        parent.Followup(MessageFactory.CreateUserText("start"));
        await parent.WhenIdle();

        var child = fixture.ChildOf(parent.Id);
        Assert.NotNull(child);
        var pin = child.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<ApprovalPolicyPayload>()
            .Single();
        Assert.Equal(ApprovalPolicy.Never, pin.Policy);
        Assert.Equal("delegation", pin.Source);
        Assert.Equal(ApprovalPolicy.Never, fixture.Approval.EffectivePolicy(child));
    }

    [Fact]
    public async Task ForkDelegation_ChildInheritsCompletedTurnPrefix()
    {
        using var fixture = new SubagentFixture(
        [
            _ => TextAnswer("parent first answer"),
            _ => ToolCallAnswer("call-sub-1", "subagent", """{"description":"fork task","prompt":"continue from the prefix"}"""),
            _ => TextAnswer("fork child answer"),
            _ => TextAnswer("parent done"),
        ],
            new SubagentToolConfig { Provider = "fork" });
        var parent = await fixture.CreateParent("session-parent-fork");
        parent.Followup(MessageFactory.CreateUserText("one"));
        await parent.WhenIdle();

        parent.Followup(MessageFactory.CreateUserText("two"));
        await parent.WhenIdle();

        var child = fixture.ChildOf(parent.Id);
        Assert.NotNull(child);
        Assert.True(child.Header.IsSeeded);
        Assert.True(child.InheritedEventCount > 0);
        Assert.Equal("fork", child.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<SubagentDescriptorPayload>()
            .Single()
            .Provider);

        var childMessages = child.DeriveMessages();
        Assert.Contains(childMessages, message =>
            message.Content.OfType<TextBlock>().Any(block => block.Text == "one"));
        Assert.Contains(childMessages, message =>
            message.Content.OfType<TextBlock>().Any(block => block.Text == "parent first answer"));

        var forkRequest = fixture.Adapter.Requests[2];
        Assert.Contains(forkRequest.Messages, message =>
            message.Content.OfType<TextBlock>().Any(block => block.Text == "parent first answer"));
        Assert.Contains(forkRequest.Messages, message =>
            message.Content.OfType<TextBlock>().Any(block => block.Text == "continue from the prefix"));
    }

    [Fact]
    public async Task ListAgents_ListsChildAfterRun()
    {
        using var fixture = new SubagentFixture(
        [
            _ => ToolCallAnswer("call-sub-1", "subagent", """{"description":"listed child","prompt":"be listed"}"""),
            _ => TextAnswer("listed answer"),
            _ => TextAnswer("done"),
        ]);
        var parent = await fixture.CreateParent("session-parent-list");
        parent.Followup(MessageFactory.CreateUserText("start"));
        await parent.WhenIdle();
        var child = fixture.ChildOf(parent.Id);
        Assert.NotNull(child);

        var result = await fixture.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-list-1"),
            Name = "list_agents",
            Arguments = JsonDocument.Parse("{}").RootElement,
            Agent = parent,
            Signal = default,
        });

        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        var row = Assert.Single(success.Value.EnumerateArray());
        Assert.Equal("child", row.GetProperty("kind").GetString());
        Assert.Equal(child.Id.Value, row.GetProperty("id").GetString());
        Assert.Equal("listed child", row.GetProperty("label").GetString());
        Assert.Equal("ready", row.GetProperty("status").GetString());
    }

    [Fact]
    public async Task NestedDelegation_TracksDepthAndDescendants()
    {
        using var fixture = new SubagentFixture(
        [
            _ => ToolCallAnswer("call-sub-1", "subagent", """{"description":"outer task","prompt":"delegate further"}"""),
            _ => ToolCallAnswer("call-sub-2", "subagent", """{"description":"inner task","prompt":"go deeper"}"""),
            _ => TextAnswer("inner answer"),
            _ => TextAnswer("outer answer"),
            _ => TextAnswer("parent done"),
        ]);
        var parent = await fixture.CreateParent("session-parent-nested");
        parent.Followup(MessageFactory.CreateUserText("start"));
        await parent.WhenIdle();

        var outer = fixture.ChildOf(parent.Id);
        Assert.NotNull(outer);
        var inner = fixture.ChildOf(outer.Id, depth: 2);
        Assert.NotNull(inner);
        Assert.Equal(2, inner.Header.DelegationDepth);
        Assert.Equal(outer.Id, inner.Header.ParentSession);

        var result = await fixture.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-list-2"),
            Name = "list_agents",
            Arguments = JsonDocument.Parse("""{"scope":"descendants"}""").RootElement,
            Agent = parent,
            Signal = default,
        });
        var success = Assert.IsType<ToolExecutionResult.Success>(result);
        var rows = success.Value.EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(outer.Id.Value, rows[0].GetProperty("id").GetString());
        Assert.Equal(1, rows[0].GetProperty("depth").GetInt32());
        Assert.Equal(inner.Id.Value, rows[1].GetProperty("id").GetString());
        Assert.Equal(2, rows[1].GetProperty("depth").GetInt32());
        Assert.Equal(outer.Id.Value, rows[1].GetProperty("parent").GetString());
    }

    [Fact]
    public async Task DepthCapExceeded_FailsNestedDelegation()
    {
        using var fixture = new SubagentFixture(
            [
                _ => ToolCallAnswer("call-sub-1", "subagent", """{"description":"outer task","prompt":"delegate further"}"""),
                _ => ToolCallAnswer("call-sub-2", "subagent", """{"description":"inner task","prompt":"go deeper"}"""),
                _ => TextAnswer("could not delegate"),
                _ => TextAnswer("parent done"),
            ],
            new SubagentToolConfig { Provider = "spawn", MaxDepth = 1 });
        var parent = await fixture.CreateParent("session-parent-cap");
        parent.Followup(MessageFactory.CreateUserText("start"));
        await parent.WhenIdle();

        var outer = fixture.ChildOf(parent.Id);
        Assert.NotNull(outer);
        var nestedError = outer.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<ToolResultPayload>()
            .Single();
        Assert.True(nestedError.Message.Block.IsError);
        Assert.Contains(
            "subagent maxDepth 1 exceeded: child would be depth 2",
            string.Concat(nestedError.Message.Block.Content.OfType<TextBlock>().Select(block => block.Text)));
        Assert.DoesNotContain(fixture.Sessions.List(), session => session.Header.DelegationDepth == 2);

        var parentToolResult = parent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<ToolResultPayload>()
            .Single();
        Assert.False(parentToolResult.Message.Block.IsError);
        Assert.Equal("could not delegate", string.Concat(parentToolResult.Message.Block.Content.OfType<TextBlock>().Select(block => block.Text)));
    }

    [Fact]
    public async Task PersonaAndToolFilter_ProjectIntoChildOnly()
    {
        using var fixture = new SubagentFixture(
            [
                _ => ToolCallAnswer("call-sub-1", "subagent", """{"description":"shaped child","prompt":"work"}"""),
                _ => TextAnswer("shaped answer"),
                _ => TextAnswer("done"),
            ],
            new SubagentToolConfig
            {
                Provider = "spawn",
                Persona = "You are the research child.",
                ToolFilter = new ToolRestriction(Deny: ["echo"]),
            });
        fixture.Tools.Register(new ToolDefinition
        {
            Name = "echo",
            Description = "echo tool",
            Parameters = new JsonObject(),
            Output = new ToolOutputDefinition(new JsonObject(), (_, value) => [new TextBlock(value.ToString())]),
            Execute = (_, _) => Task.FromResult<object?>(new { echo = true }),
        });
        var parent = await fixture.CreateParent("session-parent-shape");
        parent.Followup(MessageFactory.CreateUserText("start"));
        await parent.WhenIdle();

        var childRequest = fixture.Adapter.Requests[1];
        Assert.DoesNotContain(childRequest.Tools!, tool => tool.Name == "echo");
        Assert.Contains(childRequest.Tools!, tool => tool.Name == "subagent");
        Assert.Contains("You are the research child.", childRequest.System);
        Assert.Contains(childRequest.Messages, message =>
            message.Content.OfType<TextBlock>().Any(block =>
                block.Text.Contains("delegated subagent within your delegation scope")));

        var parentRequest = fixture.Adapter.Requests[0];
        Assert.Contains(parentRequest.Tools!, tool => tool.Name == "echo");
        Assert.DoesNotContain("You are the research child.", parentRequest.System);
    }

    [Fact]
    public async Task StructuredOutput_CapturedAndConcludesTurn()
    {
        using var fixture = new SubagentFixture(
        [
            _ => TextAnswer("parent plain answer"),
            _ => ToolCallAnswer("call-structured-1", "structured_output", """{"answer":42}"""),
        ]);
        var parent = await fixture.CreateParent("session-parent-structured");
        parent.Followup(MessageFactory.CreateUserText("hello"));
        await parent.WhenIdle();
        Assert.DoesNotContain(fixture.Adapter.Requests[0].Tools!, tool => tool.Name == "structured_output");

        var run = await fixture.Subagents.StartAsync("spawn", new SubagentStartRequest
        {
            Label = "structured run",
            Prompt = [new TextBlock("collect the answer")],
            Parent = parent,
            Signal = default,
            OutputSchema = JsonNode.Parse(
                """{"type":"object","required":["answer"],"properties":{"answer":{"type":"integer"}},"additionalProperties":false}""")!.AsObject(),
        });
        Assert.Contains(fixture.Adapter.Requests[1].Tools!, tool => tool.Name == "structured_output");

        var result = await run.Result;
        Assert.Equal(SubagentStopReason.Completed, result.StopReason);
        Assert.NotNull(result.Structured);
        Assert.Equal(42, result.Structured!.Value.GetProperty("answer").GetInt32());
        await run.DisposeAsync();

        var child = fixture.ChildOf(parent.Id);
        Assert.NotNull(child);
        var turnEnd = child.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<TurnEndPayload>()
            .Single();
        Assert.IsType<TurnEndReason.Completed>(turnEnd.Reason);
    }

    [Fact]
    public async Task ProviderRegistry_ValidatesDuplicatesCapabilitiesAndNames()
    {
        using var fixture = new SubagentFixture([]);
        var parent = await fixture.CreateParent("session-parent-registry");

        Assert.Equal(["fork", "spawn"], fixture.Subagents.List().Order(StringComparer.Ordinal));
        var duplicate = Assert.Throws<SubagentException>(() => fixture.Subagents.RegisterProvider(new SpawnInProcessProvider()));
        Assert.Equal(SubagentErrorCodes.DuplicateProvider, duplicate.Code);

        var missing = await Assert.ThrowsAsync<SubagentException>(() => fixture.Subagents.StartAsync("missing", new SubagentStartRequest
        {
            Prompt = [new TextBlock("hi")],
            Parent = parent,
            Signal = default,
        }));
        Assert.Equal(SubagentErrorCodes.NoProvider, missing.Code);

        var weak = new WeakProvider();
        fixture.Subagents.RegisterProvider(weak);
        var unsupported = await Assert.ThrowsAsync<SubagentException>(() => fixture.Subagents.StartAsync("weak", new SubagentStartRequest
        {
            Prompt = [new TextBlock("hi")],
            Parent = parent,
            Signal = default,
            Persona = "custom persona",
        }));
        Assert.Equal(SubagentErrorCodes.UnsupportedCapability, unsupported.Code);
        Assert.Equal("""subagent provider "weak" does not support persona""", unsupported.Message);
        Assert.False(weak.Started);
    }

    [Fact]
    public async Task ControlTools_InterruptAcceptedAndSendMessageUnavailable()
    {
        using var fixture = new SubagentFixture([]);
        var parent = await fixture.CreateParent("session-parent-control");
        var control = SubagentControlTools.Apply(fixture.Ctx);
        try
        {
            var interrupt = await fixture.Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("call-interrupt-1"),
                Name = "interrupt_agent",
                Arguments = JsonDocument.Parse("""{"childId":"subagent-missing"}""").RootElement,
                Agent = parent,
                Signal = default,
            });
            var accepted = Assert.IsType<ToolExecutionResult.Success>(interrupt);
            Assert.True(accepted.Value.GetProperty("accepted").GetBoolean());

            var send = await fixture.Tools.Execute(new ToolExecutionInput
            {
                CallId = ToolCallId.Create("call-send-1"),
                Name = "send_message",
                Arguments = JsonDocument.Parse("""{"content":"hello","target":"child","childId":"subagent-missing"}""").RootElement,
                Agent = parent,
                Signal = default,
            });
            var failure = Assert.IsType<ToolExecutionResult.Failure>(send);
            Assert.True(failure.IsError);
            Assert.Equal(SubagentErrorCodes.ContinuationUnavailable, failure.Error.Info?.Code);
        }
        finally
        {
            control.Dispose();
        }
    }

    private sealed class WeakProvider : ISubagentProvider
    {
        public string Name => "weak";
        public SubagentCapabilities Capabilities { get; } = new();
        public bool InheritsParentContext => false;
        public bool Started { get; private set; }

        public Task<ISubagentRun> StartAsync(ResolvedSubagentStartRequest request)
        {
            Started = true;
            throw new InvalidOperationException("must not be called");
        }
    }
}
