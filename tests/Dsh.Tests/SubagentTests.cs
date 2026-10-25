using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Subagent;

namespace Dsh.Tests;

public class SubagentTests
{
    private static readonly TimeSpan RunCompletionTimeout = TimeSpan.FromSeconds(5);
    private const string ToolSubagentPackage = "@deepseek-ai/dsh-tool-subagent";
    private const string AlternateDefaultRoute = "alt-provider/alt-fast";

    private sealed class ScriptedAdapter : LlmAdapter
    {
        private readonly Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> _script;
        private readonly IReadOnlyList<string> _models;
        private readonly LlmModelReasoningInfo? _reasoning;

        public ScriptedAdapter(
            string provider,
            IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script,
            IReadOnlyList<string>? models = null,
            LlmModelReasoningInfo? reasoning = null)
        {
            _script = new Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>>(script);
            _models = models ?? [];
            _reasoning = reasoning;
            ProviderInfo = new LlmProviderInfo(provider, provider);
        }

        public List<GenerateOptions> Requests { get; } = [];

        public override LlmProviderInfo ProviderInfo { get; }

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override IReadOnlyList<LlmModelInfo> ListModels()
            => [.. _models.Select(model => new LlmModelInfo(ProviderInfo.Id, model, model))];

        public override LlmResolvedModelInfo ResolveModel(string model)
            => new(ProviderInfo.Id, model, model, Reasoning: _reasoning);

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
        private readonly IDisposable _models;

        public SubagentFixture(
            IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script,
            SubagentToolConfig? toolConfig = null,
            IReadOnlyList<string>? models = null,
            HarnessHome? home = null,
            object? pluginConfig = null)
        {
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            _ = new AgentLoop(Ctx);
            Approval = ApprovalService.Register(Ctx);
            Subagents = new SubagentRuntime(Ctx);
            Adapter = new ScriptedAdapter("test-provider", script, models);
            Llm.RegisterAdapter(["test-provider"], Adapter);
            _spawn = SubagentInProcessProviders.RegisterSpawn(Ctx);
            _fork = SubagentInProcessProviders.RegisterFork(Ctx);
            if (home is not null)
                Ctx.SetOwn("harnessOptions", new HarnessOptions(home));
            _tool = home is null
                ? SubagentTool.Apply(Ctx, toolConfig ?? new SubagentToolConfig { Provider = "spawn" })
                : new Dsh.Subagent.Plugin(ToolSubagentPackage).Apply(Ctx, pluginConfig);
            _control = SubagentControlTools.ApplyListAgents(Ctx);
            _models = SubagentModelsTool.Apply(Ctx);
        }

        public Context Ctx { get; }
        public LlmRuntime Llm { get; }
        public SessionStore Sessions { get; }
        public ToolRuntime Tools { get; }
        public ApprovalService Approval { get; }
        private AgentRegistry Agents { get; }
        public SubagentRuntime Subagents { get; }
        public ScriptedAdapter Adapter { get; }

        public async Task<AgentLoopAgent> CreateParent(string id)
            => (AgentLoopAgent)(await CreateParentHandle(id)).Agent;

        public async Task<AgentHandle> CreateParentHandle(string id)
            => await Agents.Create(new CreateAgentOptions(
                SessionId.Create(id), null, new AgentOptions("test-provider", "test-model")));

        public Session? ChildOf(SessionId parentId, int depth = 1)
            => Sessions.List().SingleOrDefault(session =>
                session.Header.ParentSession == parentId && session.Header.DelegationDepth == depth);

        public void Dispose()
        {
            _tool.Dispose();
            _control.Dispose();
            _models.Dispose();
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

    private static List<T> CollectEvents<TNotification, T>(Context ctx, Func<TNotification, T> select)
        where TNotification : INotification
    {
        var collected = new List<T>();
        ctx.On<TNotification>(notification =>
        {
            collected.Add(select(notification));
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
        var starts = CollectEvents<SubagentStartNotification, SubagentRunInfo>(fixture.Ctx, notification => notification.Info);
        var ends = CollectEvents<SubagentEndNotification, SubagentRunEndInfo>(fixture.Ctx, notification => notification.Info);
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
        Assert.Equal(ToolCallId.Create("call-sub-1"), child.Header.ParentCallId);
        Assert.False(child.Header.IsSeeded);
        Assert.Equal(parent.Id, child.Header.RootSession);
        Assert.Equal("spawn", child.Header.SubagentProvider);
        Assert.Equal(SubagentDescriptorPayload.OneShotMode, child.Header.SubagentMode);
        Assert.Equal("research task", child.Header.SubagentLabel);

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

    /** 子 run 结束即退订其投影; 父 agent 释放时父子会话一并离开 store, 订阅数回到创建前。 */
    [Fact]
    public async Task FinishedDelegation_ReleasesSubscriptions_AndParentReleaseDropsSessions()
    {
        using var fixture = new SubagentFixture(
        [
            _ => ToolCallAnswer("call-sub-1", "subagent", """{"description":"release task","prompt":"do it"}"""),
            _ => TextAnswer("child answer"),
            _ => TextAnswer("parent done"),
        ]);
        var disposed = CollectEvents<SessionDisposedNotification, SessionId>(fixture.Ctx, notification => notification.Session.Id);
        var baseline = fixture.Ctx.EventSubscriptions.Count;
        var handle = await fixture.CreateParentHandle("session-parent-release");
        var parent = (AgentLoopAgent)handle.Agent;

        parent.Followup(MessageFactory.CreateUserText("start"));
        await parent.WhenIdle();
        var child = fixture.ChildOf(parent.Id);
        Assert.NotNull(child);
        handle.Dispose.Dispose();

        Assert.Null(fixture.Sessions.Get(parent.Id));
        Assert.Null(fixture.Sessions.Get(child.Id));
        Assert.Contains(parent.Id, disposed);
        Assert.Contains(child.Id, disposed);
        Assert.Equal(baseline, fixture.Ctx.EventSubscriptions.Count);
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
        Assert.Equal(ToolCallId.Create("call-sub-1"), outer.Header.ParentCallId);
        Assert.Equal(outer.Id, inner.Header.ParentSession);
        Assert.Equal(ToolCallId.Create("call-sub-2"), inner.Header.ParentCallId);
        Assert.Equal(parent.Id, inner.Header.RootSession);

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
        Assert.Contains(childRequest.Messages, message =>
            message.Role == MessageRole.System
            && message.Content.OfType<TextBlock>().Any(block => block.Text.Contains("You are the research child.")));
        Assert.Contains(childRequest.Messages, message =>
            message.Content.OfType<TextBlock>().Any(block =>
                block.Text.Contains("delegated subagent within your delegation scope")));

        var parentRequest = fixture.Adapter.Requests[0];
        Assert.Contains(parentRequest.Tools!, tool => tool.Name == "echo");
        Assert.DoesNotContain(parentRequest.Messages, message =>
            message.Role == MessageRole.System
            && message.Content.OfType<TextBlock>().Any(block => block.Text.Contains("You are the research child.")));
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

        var result = await run.Result.WaitAsync(RunCompletionTimeout, TestContext.Current.CancellationToken);
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

    private static Task<ToolExecutionResult> ExecuteTool(SubagentFixture fixture, string name, JsonObject args, IAgent? agent)
        => fixture.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create($"call-{Guid.NewGuid():N}"),
            Name = name,
            Arguments = JsonDocument.Parse(args.ToJsonString()).RootElement,
            Agent = agent,
            Signal = default,
        });

    private static IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> EchoRoute(int calls)
        => Enumerable.Repeat<Func<GenerateOptions, IReadOnlyList<StreamChunk>>>(
            options => TextAnswer($"{options.Provider}/{options.Model}/{options.ReasoningEffort}"), calls);

    /** Every row list_subagent_models returns is accepted unchanged by the subagent tool's model selection and reaches that route. */
    [Fact]
    public async Task ListedModelRoutes_AreAcceptedBySubagentModelSelection()
    {
        using var fixture = new SubagentFixture(EchoRoute(1), models: ["test-model"]);
        var reasoning = new LlmModelReasoningInfo(
            [new LlmReasoningEffortInfo(ReasoningEffortId.Create("low"), "Low"), new LlmReasoningEffortInfo(ReasoningEffortId.Create("high"), "High")],
            ReasoningEffortId.Create("high"));
        var alternate = new ScriptedAdapter("alt-provider", EchoRoute(2), ["alt-fast", "vendor/alt-nested"], reasoning);
        using var registration = fixture.Llm.RegisterAdapter(["alt-provider"], alternate);
        var parent = await fixture.CreateParent("session-parent-models");

        var listing = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(fixture, SubagentModelsTool.ToolName, new JsonObject(), parent));
        var rows = listing.Value.EnumerateArray().ToList();
        Assert.Equal(
            ["alt-provider/alt-fast", "alt-provider/vendor/alt-nested", "test-provider/test-model"],
            rows.Select(row => $"{row.GetProperty("provider").GetString()}/{row.GetProperty("model").GetString()}"));

        foreach (var row in rows)
        {
            var provider = row.GetProperty("provider").GetString();
            var model = row.GetProperty("model").GetString();
            var args = new JsonObject
            {
                ["description"] = "route check",
                ["prompt"] = "report your route",
                ["provider"] = provider,
                ["model"] = model,
            };
            var effort = row.TryGetProperty("reasoningEfforts", out var efforts) ? efforts[0].GetString() : null;
            if (effort is not null)
                args["reasoning_effort"] = effort;
            var delegated = Assert.IsType<ToolExecutionResult.Success>(await ExecuteTool(fixture, "subagent", args, parent));
            Assert.Equal($"{provider}/{model}/{effort}", string.Concat(delegated.Content.OfType<TextBlock>().Select(block => block.Text)));
        }
        Assert.Equal(["alt-fast", "vendor/alt-nested"], alternate.Requests.Select(request => request.Model));
        Assert.Equal(["test-model"], fixture.Adapter.Requests.Select(request => request.Model));
    }

    [Fact]
    public async Task SubagentModelSelection_RejectsHalfRoutesAndUnregisteredProviders()
    {
        using var fixture = new SubagentFixture([], models: ["test-model"]);
        var parent = await fixture.CreateParent("session-parent-model-errors");

        var half = await ExecuteTool(fixture, "subagent", new JsonObject
        {
            ["description"] = "half route",
            ["prompt"] = "p",
            ["model"] = "test-model",
        }, parent);
        Assert.Contains("must be supplied together", Assert.IsType<ToolExecutionResult.Failure>(half).Error.Message);

        var unknown = await ExecuteTool(fixture, "subagent", new JsonObject
        {
            ["description"] = "unknown route",
            ["prompt"] = "p",
            ["provider"] = "keyless-provider",
            ["model"] = "m",
        }, parent);
        Assert.Contains("keyless-provider", Assert.IsType<ToolExecutionResult.Failure>(unknown).Error.Message);

        var filtered = await ExecuteTool(fixture, SubagentModelsTool.ToolName, new JsonObject { ["provider"] = "keyless-provider" }, parent);
        Assert.Contains("providers with a configured API key: test-provider", Assert.IsType<ToolExecutionResult.Failure>(filtered).Error.Message);
        Assert.Empty(fixture.Adapter.Requests);
        Assert.Null(fixture.ChildOf(parent.Id));
    }

    /** Delegates once with settings.yaml `subagent.default_model` set; returns the child's echoed route, or the tool error. */
    private static async Task<string> DelegateWithSettingsDefault(string defaultModel, object? pluginConfig = null, JsonObject? route = null)
    {
        var home = TempTree.CreateDirectory("subagent-default-model");
        try
        {
            await File.WriteAllTextAsync(Path.Combine(home, "settings.yaml"), $"subagent:\n  default_model: {defaultModel}\n");
            using var fixture = new SubagentFixture(EchoRoute(1), models: ["test-model"], home: new HarnessHome(home), pluginConfig: pluginConfig);
            var alternate = new ScriptedAdapter("alt-provider", EchoRoute(1), ["alt-fast", "vendor/alt-nested"]);
            using var registration = fixture.Llm.RegisterAdapter(["alt-provider"], alternate);
            var parent = await fixture.CreateParent($"session-parent-default-model-{Guid.NewGuid():N}");
            var args = new JsonObject { ["description"] = "default route", ["prompt"] = "report your route" };
            foreach ((var key, var value) in route ?? [])
                args[key] = value?.DeepClone();
            return await ExecuteTool(fixture, "subagent", args, parent) switch
            {
                ToolExecutionResult.Success delegated => string.Concat(delegated.Content.OfType<TextBlock>().Select(block => block.Text)),
                ToolExecutionResult.Failure failure => failure.Error.Message,
                var other => throw new InvalidOperationException($"unexpected tool result {other}"),
            };
        }
        finally
        {
            TempTree.Delete(home);
        }
    }

    /** Without route arguments or plugin agentOptions, the child runs on settings.yaml `subagent.default_model` with that model's default effort. */
    [Fact]
    public async Task SubagentDefaultModelSetting_RoutesChildWhenCallOmitsRoute()
        => Assert.Equal("alt-provider/alt-fast/", await DelegateWithSettingsDefault(AlternateDefaultRoute));

    [Fact]
    public async Task SubagentDefaultModelSetting_YieldsToPluginAgentOptionsAndCallArguments()
    {
        var pluginRoute = new Dictionary<string, object?>
        {
            ["agentOptions"] = new Dictionary<string, object?> { ["provider"] = "alt-provider", ["model"] = "vendor/alt-nested" },
        };
        Assert.Equal("alt-provider/vendor/alt-nested/", await DelegateWithSettingsDefault(AlternateDefaultRoute, pluginRoute));
        var callRoute = new JsonObject { ["provider"] = "alt-provider", ["model"] = "vendor/alt-nested" };
        Assert.Equal("alt-provider/vendor/alt-nested/", await DelegateWithSettingsDefault(AlternateDefaultRoute, route: callRoute));
    }

    [Fact]
    public async Task SubagentDefaultModelSetting_UnregisteredProviderErrorNamesTheSetting()
    {
        var error = await DelegateWithSettingsDefault("keyless-provider/keyless-model");
        Assert.Contains("keyless-provider", error);
        Assert.Contains("subagent.default_model", error);
    }

    private sealed class NoCredentials : ICredentials
    {
        public string? Get(string reference) => null;
    }

    /** Providers are listed through the real settings registration path: one without an API key never appears. */
    [Fact]
    public async Task ListSubagentModels_OmitsProvidersWithoutApiKey()
    {
        var ctx = new Context();
        _ = new SystemPrompt(ctx, new SystemPromptConfig());
        var tools = new ToolRuntime(ctx);
        _ = new LlmRuntime(ctx);
        _ = new LlmAdapterFactoryRegistry(ctx);
        ctx.SetOwn("credentials", new NoCredentials());
        using var openAi = new Dsh.Llm.OpenAi.Plugin().Apply(ctx, null);
        var options = new HarnessOptions(new HarnessHome(AppContext.BaseDirectory));
        const string unreachableBaseUrl = "http://127.0.0.1:9/v1";
        var keyed = ProviderRegistrar.RegisterProvider(ctx, options, "keyed-provider", new ProviderSettings
        {
            Options = new ProviderOptions { BaseUrl = unreachableBaseUrl, ApiKey = "test-only-key" },
            Models = { ["keyed-model"] = new ProviderModelSettings { Name = "Keyed Model" } },
        }, isDefault: false);
        var keyless = ProviderRegistrar.RegisterProvider(ctx, options, "keyless-provider", new ProviderSettings
        {
            Options = new ProviderOptions { BaseUrl = unreachableBaseUrl, ApiKeyEnv = "DSH_TEST_UNSET_API_KEY" },
            Models = { ["keyless-model"] = new ProviderModelSettings { Name = "Keyless Model" } },
        }, isDefault: false);
        using var keyedHandle = keyed.Handle;
        Assert.NotNull(keyedHandle);
        Assert.Null(keyless.Handle);
        using var listTool = SubagentModelsTool.Apply(ctx);

        var result = await tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-list-models-keyless"),
            Name = SubagentModelsTool.ToolName,
            Arguments = JsonDocument.Parse("{}").RootElement,
            Signal = default,
        });

        var row = Assert.Single(Assert.IsType<ToolExecutionResult.Success>(result).Value.EnumerateArray());
        Assert.Equal("keyed-provider", row.GetProperty("provider").GetString());
        Assert.Equal("keyed-model", row.GetProperty("model").GetString());
        Assert.Equal("Keyed Model", row.GetProperty("name").GetString());
        Assert.DoesNotContain("keyless", string.Concat(result.Content.OfType<TextBlock>().Select(block => block.Text)));
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
