using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Compaction;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.SessionGraph;
using Dsh.Subagent;
using Dsh.Workflow;

namespace Dsh.Tests;

/**
 * 事件图组件测试: 会话日志一律由真实写入路径产生(agent 循环 + 工具/审批/命令/压缩/子代理/工作流/fork),
 * 再从 ISessionEventGraph 读出节点与边; LLM 由脚本化适配器按顺序应答。
 */
public sealed class SessionEventGraphTests : IDisposable
{
    private const string Provider = "test-provider";
    private const string Model = "test-model";
    private const string ProbeTool = "probe";
    private const string SummaryText = "## Current Work\n- graph summary";
    private const int CompactionContextWindow = 4000;
    private const int PrunableResultChars = 20000;
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] CompactableTurns =
    [
        new string('u', 14000),
        new string('o', 400),
        new string('p', 400),
        new string('q', 400),
    ];

    private readonly string _directory = TempTree.CreateDirectory("session-graph");

    public void Dispose() => TempTree.Delete(_directory);

    private sealed class ScriptedAdapter(IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script) : LlmAdapter
    {
        private readonly Queue<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> _script = new(script);

        public override LlmProviderInfo ProviderInfo => new(Provider, Provider);

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override LlmResolvedModelInfo ResolveModel(string model)
            => new(Provider, model, model, ContextWindow: CompactionContextWindow);

        public override IAsyncEnumerable<StreamChunk> Stream(GenerateOptions options, CancellationToken cancellationToken)
        {
            if (options.Purpose == GeneratePurpose.Compaction)
                return Yield(TextAnswer(SummaryText), cancellationToken);
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

    private sealed class Fixture : IDisposable
    {
        private readonly List<IDisposable> _owned = [];

        public Fixture(string directory, IEnumerable<Func<GenerateOptions, IReadOnlyList<StreamChunk>>> script)
        {
            Directory = directory;
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            var llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            _ = new AgentLoop(Ctx);
            _ = ApprovalService.Register(Ctx);
            Commands = CommandsService.Register(Ctx);
            _ = new TokenMeter(Ctx);
            Pruner = new ToolResultPruner(Ctx);
            _owned.Add(new BasicCompactionEngine(Ctx, new BasicCompactionConfig { Auto = false }));
            _owned.Add(CompactCommand.Register(Ctx));
            _ = new SubagentRuntime(Ctx);
            _owned.Add(SubagentInProcessProviders.RegisterSpawn(Ctx));
            _owned.Add(SubagentTool.Apply(Ctx, new SubagentToolConfig { Provider = "spawn" }));
            _owned.Add(new SubprocessService(Ctx));
            _ = new WorkerThreadWorkflowEngine(Ctx, null);
            _owned.Add(ToolWorkflow.Apply(Ctx, new ToolWorkflowConfig()));
            Persistence = new JsonlSessionPersistence(Path.Combine(directory, "sessions"));
            PersistenceReads = new CountingPersistence(Persistence);
            Ctx.Provide(ISessionPersistence.ServiceName, PersistenceReads);
            var graph = new SessionEventGraphService(Ctx);
            Graph = graph;
            _owned.Add(graph);
            RegisterProbeTool();
            llm.RegisterAdapter([Provider], new ScriptedAdapter(script));
        }

        public string Directory { get; }
        public Context Ctx { get; }
        public SessionStore Sessions { get; }
        public ToolRuntime Tools { get; }
        public AgentRegistry Agents { get; }
        public CommandsService Commands { get; }
        public ToolResultPruner Pruner { get; }
        public JsonlSessionPersistence Persistence { get; }
        public CountingPersistence PersistenceReads { get; }
        public ISessionEventGraph Graph { get; }

        /** probe 工具回显参数里的 text, 且每次执行前都要审批(自动批准), 让日志里出现审批配对。 */
        private void RegisterProbeTool()
        {
            Tools.Register(new ToolDefinition
            {
                Name = ProbeTool,
                Description = "echo the text argument",
                Parameters = new JsonObject(),
                Output = new ToolOutputDefinition(new JsonObject(), (_, value) => [new TextBlock(value.GetProperty("text").GetString()!)]),
                Execute = (args, _) => Task.FromResult<object?>(new { text = args.GetProperty("text").GetString() }),
            });
            Ctx.OnWaterfall<ToolPreExecuteNotification>(
                (notification, next) => notification.Run.Name == ProbeTool
                    ? ValueTask.FromResult<object?>(new PreToolDecision.Ask())
                    : next(),
                new EventOptions { Global = true });
            _owned.Add(ApprovalAnswerers.AutoApprove(Ctx));
        }

        public async Task<AgentLoopAgent> CreateAgent(string id)
        {
            var handle = await Agents.Create(new CreateAgentOptions(SessionId.Create(id), null, new AgentOptions(Provider, Model)));
            return (AgentLoopAgent)handle.Agent;
        }

        public SessionId ForkThrough(Session source, long seq)
            => SessionFork.Fork(Persistence, source, seq + 1, "graph fork", Directory);

        public void Dispose()
        {
            foreach (var owned in Enumerable.Reverse(_owned))
                owned.Dispose();
            Persistence.Dispose();
        }
    }

    private sealed class CountingPersistence(ISessionPersistence inner) : ISessionPersistence
    {
        public List<SessionId> StatCalls { get; } = [];
        public int ListCalls { get; private set; }
        public ISessionHandle Create(SessionHeader header, long? inheritedEventCount = null) => inner.Create(header, inheritedEventCount);
        public ISessionHandle Open(SessionId id, SessionAccess access) => inner.Open(id, access);
        public SessionPersistenceSnapshot? Stat(SessionId id)
        {
            StatCalls.Add(id);
            return inner.Stat(id);
        }
        public IReadOnlyList<SessionPersistenceSnapshot> List()
        {
            ListCalls++;
            return inner.List();
        }
        public void Rename(SessionId id, string title) => inner.Rename(id, title);
        public void Delete(SessionId id) => inner.Delete(id);
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

    private static Func<GenerateOptions, IReadOnlyList<StreamChunk>> Probe(string callId, string text)
        => _ => ToolCallAnswer(callId, ProbeTool, JsonSerializer.Serialize(new { text }));

    private static Func<GenerateOptions, IReadOnlyList<StreamChunk>> Reply(string text) => _ => TextAnswer(text);

    private static async Task RunTurn(AgentLoopAgent agent, string text)
    {
        agent.Followup(MessageFactory.CreateUserText(text));
        await agent.WhenIdle();
    }

    private static long SeqOf<T>(Session session, Func<T, bool>? match = null) where T : SessionEventPayload
        => session.SnapshotEvents().Single(e => e.Data is T payload && (match?.Invoke(payload) ?? true)).Seq;

    private static long CallSeq(Session session, string callId)
        => SeqOf<ToolCallPayload>(session, payload => payload.CallId.Value == callId);

    /** 该调用的第一条结果(被裁剪替换前的原始结果)。 */
    private static long ResultSeq(Session session, string callId)
        => session.SnapshotEvents().First(e => e.Data is ToolResultPayload payload && payload.Message.ToolSource.CallId.Value == callId).Seq;

    private static IReadOnlyList<SessionEventEdge> Edges(ISessionEventGraph graph, SessionId session, long seq, SessionEventEdgeKind kind)
        => [.. graph.EdgesFrom(session, seq).Where(edge => edge.Kind == kind)];

    /** 两张图在 [0, count) 上的节点(surface)与出边逐条一致。 */
    private static void AssertSameGraph(ISessionEventGraph graph, SessionId expected, SessionId actual, long count)
    {
        for (var seq = 0L; seq < count; seq++)
        {
            Assert.Equal(graph.Node(expected, seq)?.Surface, graph.Node(actual, seq)?.Surface);
            Assert.Equal(graph.Node(expected, seq)?.Type, graph.Node(actual, seq)?.Type);
            Assert.Equal(graph.EdgesFrom(expected, seq), graph.EdgesFrom(actual, seq));
        }
    }

    [Fact]
    public async Task ToolCall_Result_And_Approval_PairWithTheirCall()
    {
        using var fixture = new Fixture(_directory, [Probe("call-probe", "echoed"), Reply("done")]);
        var agent = await fixture.CreateAgent("graph-tool-call");

        await RunTurn(agent, "use the probe");

        var session = agent.Session;
        var call = CallSeq(session, "call-probe");
        var result = ResultSeq(session, "call-probe");
        var asked = SeqOf<ApprovalAskedPayload>(session);
        var decided = SeqOf<ApprovalDecidedPayload>(session);
        var graph = fixture.Graph;
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.ResultOf, result, call)], Edges(graph, session.Id, result, SessionEventEdgeKind.ResultOf));
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.Cites, result, call)], Edges(graph, session.Id, result, SessionEventEdgeKind.Cites));
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.Approves, asked, call)], graph.EdgesFrom(session.Id, asked));
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.Completes, decided, asked)], graph.EdgesFrom(session.Id, decided));
        Assert.Equal(
            [SessionEventEdgeKind.Cites, SessionEventEdgeKind.ResultOf, SessionEventEdgeKind.Approves],
            graph.EdgesTo(session.Id, call).Select(edge => edge.Kind).Order());
        Assert.Equal(SessionEventSurface.Current, graph.Node(session.Id, result)?.Surface);
        Assert.Equal(SessionEventSurface.LogOnly, graph.Node(session.Id, call)?.Surface);
        Assert.Null(graph.Node(session.Id, session.Seq));
    }

    [Fact]
    public async Task CompactCommand_ReplacesHistory_AndPairsCommandAndCompaction()
    {
        using var fixture = new Fixture(_directory, CompactableTurns.Select(_ => Reply(new string('x', 200))));
        var agent = await fixture.CreateAgent("graph-compaction");
        foreach (var turn in CompactableTurns)
            await RunTurn(agent, turn);

        var execution = await fixture.Commands.Execute(agent, "/compact", TestContext.Current.CancellationToken);

        Assert.IsType<CommandResult.Success>(execution!.Result);
        var session = agent.Session;
        var summarySeq = SeqOf<CompactionSummaryPayload>(session);
        var shadowed = ((CompactionSummaryPayload)session.EventAt(summarySeq)!.Data).ShadowedSeqs;
        var start = SeqOf<CompactionStartPayload>(session);
        var checkpoint = SeqOf<UserMessagePayload>(session, payload => CompactionCheckpoint.IsCheckpointSource(payload.Message.Source));
        var graph = fixture.Graph;
        Assert.Equal(shadowed, Edges(graph, session.Id, checkpoint, SessionEventEdgeKind.Replaces).Select(edge => edge.To));
        Assert.Equal([start, summarySeq], Edges(graph, session.Id, checkpoint, SessionEventEdgeKind.Cites).Select(edge => edge.To));
        Assert.All(shadowed, seq => Assert.Equal(SessionEventSurface.Shadowed, graph.Node(session.Id, seq)?.Surface));
        Assert.Equal(SessionEventSurface.Current, graph.Node(session.Id, checkpoint)?.Surface);
        Assert.Equal(
            [new SessionEventEdge(SessionEventEdgeKind.Completes, SeqOf<CompactionEndPayload>(session), start)],
            graph.EdgesFrom(session.Id, SeqOf<CompactionEndPayload>(session)));
        Assert.Equal(
            [new SessionEventEdge(SessionEventEdgeKind.Completes, SeqOf<CommandDonePayload>(session), SeqOf<CommandRunPayload>(session))],
            graph.EdgesFrom(session.Id, SeqOf<CommandDonePayload>(session)));
    }

    [Fact]
    public async Task PrunedToolResult_FormsReplacementChain_UnderLaterCompaction()
    {
        var script = new List<Func<GenerateOptions, IReadOnlyList<StreamChunk>>>
        {
            Probe("call-big", new string('a', PrunableResultChars)),
            Reply("noted"),
        };
        script.AddRange(CompactableTurns.Skip(1).Select(_ => Reply(new string('x', 200))));
        using var fixture = new Fixture(_directory, script);
        var agent = await fixture.CreateAgent("graph-prune");
        await RunTurn(agent, CompactableTurns[0]);
        var original = ResultSeq(agent.Session, "call-big");

        var pruned = Assert.Single(fixture.Pruner.PruneSession(agent.Session).Pruned);
        foreach (var turn in CompactableTurns.Skip(1))
            await RunTurn(agent, turn);
        await fixture.Commands.Execute(agent, "/compact", TestContext.Current.CancellationToken);

        var session = agent.Session;
        var graph = fixture.Graph;
        var checkpoint = SeqOf<UserMessagePayload>(session, payload => CompactionCheckpoint.IsCheckpointSource(payload.Message.Source));
        var call = CallSeq(session, "call-big");
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.Replaces, pruned.ReplacementSeq, original)], Edges(graph, session.Id, pruned.ReplacementSeq, SessionEventEdgeKind.Replaces));
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.ResultOf, pruned.ReplacementSeq, call)], Edges(graph, session.Id, pruned.ReplacementSeq, SessionEventEdgeKind.ResultOf));
        Assert.Contains(new SessionEventEdge(SessionEventEdgeKind.Replaces, checkpoint, pruned.ReplacementSeq), graph.EdgesTo(session.Id, pruned.ReplacementSeq));
        Assert.DoesNotContain(graph.EdgesTo(session.Id, original), edge => edge.From == checkpoint);
        Assert.Equal(SessionEventSurface.Shadowed, graph.Node(session.Id, original)?.Surface);
        Assert.Equal(SessionEventSurface.Shadowed, graph.Node(session.Id, pruned.ReplacementSeq)?.Surface);
    }

    [Fact]
    public async Task StandardSubagent_ParentCallId_LocatesItsParentToolCall()
    {
        using var fixture = new Fixture(_directory,
        [
            _ => ToolCallAnswer("call-sub", "subagent", """{"description":"graph task","prompt":"do the delegated thing"}"""),
            Reply("child answer"),
            Reply("parent done"),
        ]);
        var parent = await fixture.CreateAgent("graph-subagent-parent");
        var created = new TaskCompletionSource<(SessionLineage? Parent, IReadOnlyList<SessionLineage> Children, bool HasResult)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unsubscribe = fixture.Ctx.On<SessionCreatedNotification>(notification =>
        {
            if (notification.Session.Header.ParentSession == parent.Id)
                created.TrySetResult((fixture.Graph.Parent(notification.Session.Id), fixture.Graph.Children(parent.Id),
                    parent.Session.SnapshotEvents().Any(sessionEvent => sessionEvent.Data is ToolResultPayload)));
        }, new EventOptions { Global = true });
        try
        {
            await RunTurn(parent, "delegate");
        }
        finally
        {
            unsubscribe();
        }

        var child = fixture.Sessions.List().Single(session => session.Header.ParentSession == parent.Id);
        var graph = fixture.Graph;
        var parentCallSeq = CallSeq(parent.Session, "call-sub");
        Assert.Equal(ToolCallId.Create("call-sub"), child.Header.ParentCallId);
        Assert.DoesNotContain(
            parent.Session.SnapshotEvents().SelectMany(sessionEvent => SessionEventRelations.Of(sessionEvent.Data)),
            relation => relation is SessionEventRelation.Spawns spawns && spawns.Child == child.Id);
        var lineage = new SessionLineage(child.Id, SessionLineageKind.Subagent, parent.Id, parentCallSeq);
        var beforeResult = await created.Task.WaitAsync(EventTimeout, TestContext.Current.CancellationToken);
        Assert.False(beforeResult.HasResult);
        Assert.Equal(lineage, beforeResult.Parent);
        Assert.Equal([lineage], beforeResult.Children);
        Assert.Equal(lineage, graph.Parent(child.Id));
        Assert.Equal([lineage], graph.Children(parent.Id));
        var result = ResultSeq(parent.Session, "call-sub");
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.ResultOf, result, parentCallSeq)], Edges(graph, parent.Id, result, SessionEventEdgeKind.ResultOf));
        Assert.Null(graph.Parent(parent.Id));
        foreach (var session in new[] { parent.Session, child })
        {
            using var handle = fixture.Persistence.Create(session.Header, session.InheritedEventCount);
            handle.Append(session.SnapshotEvents());
            handle.Close();
        }
        fixture.Sessions.Release(parent.Id);
        Assert.Equal(lineage, graph.Parent(child.Id));
        fixture.PersistenceReads.StatCalls.Clear();
        Assert.Equal([lineage], graph.Children(parent.Id));
        Assert.Equal([parent.Id], fixture.PersistenceReads.StatCalls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing-call")]
    public void SubagentWithoutMatchingCorrelation_KeepsAnUnknownParentSeq(string? callId)
    {
        using var fixture = new Fixture(_directory, []);
        var parent = fixture.Sessions.Create(SessionId.Create("legacy-parent"));
        parent.Append(new ToolCallPayload(1, 1, ToolCallId.Create("other-call"), ProbeTool, "{}"));
        var childId = SessionId.Create("legacy-child");
        fixture.Sessions.Create(childId, header: parent.Header with
        {
            Id = childId,
            ParentSession = parent.Id,
            ParentCallId = callId is null ? null : ToolCallId.Create(callId),
            Origin = "subagent",
        });
        var lineage = new SessionLineage(childId, SessionLineageKind.Subagent, parent.Id, null);
        Assert.Equal(lineage, fixture.Graph.Parent(childId));
        Assert.Equal([lineage], fixture.Graph.Children(parent.Id));
    }

    [Fact]
    public async Task Children_UsesLiveAndListedSnapshots_WithoutChildStats()
    {
        using var fixture = new Fixture(_directory, [Probe("call-seed", "seed"), Reply("done")]);
        var parent = await fixture.CreateAgent("snapshot-parent");
        await RunTurn(parent, "seed the forks");
        foreach (var (id, createdAt, count) in new[] { ("b", 20L, 2L), ("a", 20L, 1L), ("z", 10L, 3L), ("moved", 0L, 1L) })
        {
            var childId = SessionId.Create(id);
            var session = Session.Create(childId, parent.Session.SnapshotEvents(0, count),
                parent.Session.Header with { Id = childId, ParentSession = parent.Id, CreatedAt = createdAt, IsSeeded = true }, count);
            using var handle = fixture.Persistence.Create(session.Header, count);
            handle.Append(session.SnapshotEvents());
            handle.Close();
        }
        var listed = fixture.Persistence.List();
        var liveSnapshot = listed.Single(snapshot => snapshot.Header.Id.Value == "z");
        using (var handle = fixture.Persistence.Open(liveSnapshot.Header.Id, SessionAccess.Read))
            fixture.Sessions.Create(liveSnapshot.Header.Id, handle.Read(), liveSnapshot.Header with { CreatedAt = 30 }, inheritedEventCount: 1);
        var moved = listed.Single(snapshot => snapshot.Header.Id.Value == "moved");
        var otherParent = SessionId.Create("other-parent");
        using (var handle = fixture.Persistence.Open(moved.Header.Id, SessionAccess.Read))
            fixture.Sessions.Create(moved.Header.Id, handle.Read(), moved.Header with { ParentSession = otherParent }, moved.InheritedEventCount);

        var children = fixture.Graph.Children(parent.Id);

        Assert.Equal(1, fixture.PersistenceReads.ListCalls);
        Assert.Empty(fixture.PersistenceReads.StatCalls);
        Assert.Equal(new[] { "a", "b", "z" }, children.Select(child => child.Session.Value));
        Assert.Equal(new long?[] { 0, 1, 0 }, children.Select(child => child.ParentSeq));
        Assert.All(children, child => Assert.Equal(child, fixture.Graph.Parent(child.Session)));
        Assert.Equal([fixture.Graph.Parent(moved.Header.Id)!.Value], fixture.Graph.Children(otherParent));
    }

    [Fact]
    public async Task WorkflowRun_PairsLifecycle_AndPinsChildToAgentStart()
    {
        const string arguments = """
            {
              "script": "var a = await agent(\"child one\"); return new JsonObject { [\"a\"] = a };",
              "meta": { "name": "graph-workflow", "description": "graph workflow" }
            }
            """;
        using var fixture = new Fixture(_directory,
        [
            _ => ToolCallAnswer("call-workflow", "workflow", arguments),
            Reply("child one answer"),
            Reply("parent done"),
        ]);
        var parent = await fixture.CreateAgent("graph-workflow-parent");

        await RunTurn(parent, "run the workflow");

        var session = parent.Session;
        var runStart = SeqOf<ToolWorkflowRunStartPayload>(session);
        var runEnd = SeqOf<ToolWorkflowRunEndPayload>(session);
        var agentStart = SeqOf<ToolWorkflowAgentStartPayload>(session);
        var agentEnd = SeqOf<ToolWorkflowAgentEndPayload>(session);
        var childId = ((ToolWorkflowAgentStartPayload)session.EventAt(agentStart)!.Data).ChildId;
        var graph = fixture.Graph;
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.Completes, runEnd, runStart)], graph.EdgesFrom(session.Id, runEnd));
        Assert.Equal([new SessionEventEdge(SessionEventEdgeKind.Completes, agentEnd, agentStart)], graph.EdgesFrom(session.Id, agentEnd));
        var lineage = new SessionLineage(childId, SessionLineageKind.Subagent, parent.Id, agentStart);
        Assert.Equal(lineage, graph.Parent(childId));
        Assert.Equal([lineage], graph.Children(parent.Id));
    }

    [Fact]
    public async Task Fork_IsBuiltFromItsStoredLog_AndPointsAtTheForkPoint()
    {
        using var fixture = new Fixture(_directory, [Probe("call-before-fork", "first"), Reply("first done"), Reply("second done")]);
        var agent = await fixture.CreateAgent("graph-fork-source");
        await RunTurn(agent, "first turn");
        await RunTurn(agent, "second turn");
        var forkPoint = ResultSeq(agent.Session, "call-before-fork");

        var forkId = fixture.ForkThrough(agent.Session, forkPoint);

        var graph = fixture.Graph;
        var lineage = new SessionLineage(forkId, SessionLineageKind.Fork, agent.Id, forkPoint);
        Assert.Equal(lineage, graph.Parent(forkId));
        Assert.Equal([lineage], graph.Children(agent.Id));
        Assert.True(graph.Node(forkId, forkPoint)?.Inherited);
        Assert.Equal(SessionEventTypes.SessionEndSeed, graph.Node(forkId, forkPoint + 1)?.Type);
        Assert.False(graph.Node(forkId, forkPoint + 1)?.Inherited);
        Assert.Null(graph.Node(forkId, forkPoint + 2));
        AssertSameGraph(graph, agent.Id, forkId, forkPoint + 1);
    }

    [Fact]
    public async Task LiveGraph_FollowsNewEvents_AndMatchesAGraphBuiltFromScratch()
    {
        using var fixture = new Fixture(_directory, [Probe("call-one", "one"), Reply("one done"), Probe("call-two", "two"), Reply("two done")]);
        var agent = await fixture.CreateAgent("graph-incremental");
        await RunTurn(agent, "first turn");
        var graph = fixture.Graph;
        var firstResult = ResultSeq(agent.Session, "call-one");
        Assert.NotEmpty(graph.EdgesFrom(agent.Id, firstResult));

        await RunTurn(agent, "second turn");

        var session = agent.Session;
        var secondResult = ResultSeq(session, "call-two");
        Assert.Contains(new SessionEventEdge(SessionEventEdgeKind.ResultOf, secondResult, CallSeq(session, "call-two")), graph.EdgesFrom(agent.Id, secondResult));
        var rebuilt = fixture.ForkThrough(session, session.Seq - 1);
        AssertSameGraph(graph, agent.Id, rebuilt, session.Seq);
    }

    /** 跑完四轮对话后执行 /compact, 返回压缩遮蔽的节点与 command/run 的 seq(截断到这里即撤销压缩)。 */
    private static async Task<(AgentLoopAgent Agent, IReadOnlyList<long> Shadowed, long CommandRun)> CompactedAgent(Fixture fixture, string id)
    {
        var agent = await fixture.CreateAgent(id);
        foreach (var turn in CompactableTurns)
            await RunTurn(agent, turn);
        await fixture.Commands.Execute(agent, "/compact", TestContext.Current.CancellationToken);
        var session = agent.Session;
        var shadowed = ((CompactionSummaryPayload)session.EventAt(SeqOf<CompactionSummaryPayload>(session))!.Data).ShadowedSeqs;
        Assert.Equal(SessionEventSurface.Shadowed, fixture.Graph.Node(agent.Id, shadowed[0])?.Surface);
        return (agent, shadowed, SeqOf<CommandRunPayload>(session));
    }

    [Fact]
    public async Task Truncation_RestoresShadowedNodes_AndDropsRemovedEvents()
    {
        using var fixture = new Fixture(_directory, CompactableTurns.Select(_ => Reply(new string('x', 200))));
        var (agent, shadowed, commandRun) = await CompactedAgent(fixture, "graph-truncate");

        Assert.True(agent.Session.TryTruncateTo(commandRun));

        var graph = fixture.Graph;
        Assert.All(shadowed, seq => Assert.Equal(SessionEventSurface.Current, graph.Node(agent.Id, seq)?.Surface));
        Assert.All(shadowed, seq => Assert.Empty(graph.EdgesTo(agent.Id, seq)));
        Assert.Null(graph.Node(agent.Id, commandRun));
    }

    /** 截断后不经查询就长回超过原长度: 只能靠截断通知发现旧图已不是日志前缀。 */
    [Fact]
    public async Task Truncation_ThenRegrowth_ReusesSeqsWithoutStaleEdges()
    {
        var script = CompactableTurns.Select(_ => Reply(new string('x', 200))).ToList();
        script.AddRange([Probe("call-after-revert", "after"), Reply("after done")]);
        using var fixture = new Fixture(_directory, script);
        var (agent, shadowed, commandRun) = await CompactedAgent(fixture, "graph-regrow");
        var session = agent.Session;
        var lengthBeforeRevert = session.Seq;

        Assert.True(session.TryTruncateTo(commandRun));
        await RunTurn(agent, "after revert");

        Assert.True(session.Seq > lengthBeforeRevert);
        var graph = fixture.Graph;
        Assert.All(shadowed, seq => Assert.Equal(SessionEventSurface.Current, graph.Node(agent.Id, seq)?.Surface));
        var result = ResultSeq(session, "call-after-revert");
        Assert.Contains(new SessionEventEdge(SessionEventEdgeKind.ResultOf, result, CallSeq(session, "call-after-revert")), graph.EdgesFrom(agent.Id, result));
        var rebuilt = fixture.ForkThrough(session, session.Seq - 1);
        AssertSameGraph(graph, agent.Id, rebuilt, session.Seq);
    }

    [Fact]
    public void UnknownEvent_IsKeptAsANode_WithoutEdges()
    {
        using var fixture = new Fixture(_directory, []);
        var source = fixture.Sessions.Create(SessionId.Create("graph-unknown"));
        var callId = ToolCallId.Create("call-around-unknown");
        source.Append(new TurnStartPayload(1));
        var call = source.Append(new ToolCallPayload(1, 1, callId, ProbeTool, "{}")).Seq;
        var unknown = source.Append(new UnknownSessionEventPayload("graph-probe/annotation", JsonDocument.Parse("""{"note":"from a newer harness"}""").RootElement.Clone())).Seq;
        var result = source.Append(
            new ToolResultPayload(1, 1, MessageFactory.CreateToolResultMessage(callId, [new TextBlock("ok")], false)),
            new SurfaceOp.Append(),
            [call]).Seq;
        var storedId = SessionId.Create("graph-unknown-stored");
        using (var handle = fixture.Persistence.Create(source.Header with { Id = storedId }))
        {
            // 当前写路径不会给事件标 ignorable, 这里按"新版本插件写出的可忽略事件"落盘, 读回时它是 UnknownSessionEventPayload。
            handle.Append([.. source.SnapshotEvents().Select(e => e.Data is UnknownSessionEventPayload ? e with { Ignorable = true } : e)]);
            handle.Close();
        }

        var graph = fixture.Graph;
        var node = graph.Node(storedId, unknown);
        Assert.IsType<UnknownSessionEventPayload>(node?.Event.Data);
        Assert.Equal("graph-probe/annotation", node?.Type);
        Assert.Equal(SessionEventSurface.LogOnly, node?.Surface);
        Assert.Empty(graph.EdgesFrom(storedId, unknown));
        Assert.Empty(graph.EdgesTo(storedId, unknown));
        Assert.Contains(new SessionEventEdge(SessionEventEdgeKind.ResultOf, result, call), graph.EdgesFrom(storedId, result));
    }
}
