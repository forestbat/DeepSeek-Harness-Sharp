using System.Runtime.CompilerServices;
using System.Text.Json;
using Dsh.Runtime;
using Dsh.Compaction;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

public class CompactionTests
{
    private const string Provider = "test-provider";
    private const string Model = "test-model";
    private const string SummaryText = "## Current Work\n- fixed summary";

    private sealed class MockSummaryAdapter : LlmAdapter
    {
        private readonly int _contextWindow;
        private readonly string _turnText;
        private readonly string _summaryText;

        public MockSummaryAdapter(int contextWindow, string turnText, string summaryText = SummaryText)
        {
            _contextWindow = contextWindow;
            _turnText = turnText;
            _summaryText = summaryText;
        }

        public int CompactionCalls { get; private set; }

        public int TurnCalls { get; private set; }

        public GenerateOptions? LastCompactionOptions { get; private set; }

        public List<GenerateOptions> CompactionOptions { get; } = [];

        public Func<int, Exception?>? CompactionError { get; set; }

        /** 摘要请求的结束原因(默认 Stop); 用 MaxTokens 验证"N2 截断仍被接受"。 */
        public Func<int, FinishReason>? CompactionFinish { get; set; }

        /** 覆盖摘要正文, 用于构造"摘要比原文更大"这类降级场景。 */
        public string? SummaryOverride { get; set; }

        /** 模型声明的推理档位; null 表示该模型不提供推理(摘要请求就该原样走默认)。 */
        public LlmModelReasoningInfo? Reasoning { get; set; }

        /** 对话请求回报的用量(模拟 provider 的真实 usage, 含缓存命中)。 */
        public TokenUsage? Usage { get; set; }

        public TaskCompletionSource TurnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseTurn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool GateTurns { get; set; }

        public override LlmProviderInfo ProviderInfo => new(Provider, "Test Provider");

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override LlmResolvedModelInfo ResolveModel(string model)
            => new(Provider, model, model, ContextWindow: _contextWindow, Reasoning: Reasoning);

        public override async IAsyncEnumerable<StreamChunk> Stream(
            GenerateOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string text;
            if (options.Purpose == GeneratePurpose.Compaction)
            {
                CompactionCalls++;
                LastCompactionOptions = options;
                CompactionOptions.Add(options);
                if (CompactionError?.Invoke(CompactionCalls - 1) is { } error)
                    throw error;
                text = SummaryOverride ?? _summaryText;
            }
            else
            {
                TurnCalls++;
                text = _turnText;
                if (GateTurns)
                {
                    TurnStarted.TrySetResult();
                    await ReleaseTurn.Task;
                }
            }
            yield return new StreamChunk.BlockStart(0, "text");
            yield return new StreamChunk.TextDelta(0, text);
            yield return new StreamChunk.BlockEnd(0, new TextBlock(text));
            if (options.Purpose != GeneratePurpose.Compaction && Usage is { } usage)
                yield return new StreamChunk.Usage(usage);
            var finish = options.Purpose == GeneratePurpose.Compaction
                ? CompactionFinish?.Invoke(CompactionCalls - 1) ?? new FinishReason.Stop()
                : new FinishReason.Stop();
            yield return new StreamChunk.Finish(finish);
        }
    }

    private sealed class Harness : IDisposable
    {
        public Context Ctx { get; }
        public SessionStore Sessions { get; }
        public LlmRuntime Llm { get; }
        public AgentRegistry Agents { get; }
        public ToolResultPruner Pruner { get; }
        public TokenMeter Meter { get; }
        public ToolRuntime Tools { get; }
        public SystemPrompt Prompt { get; }
        public BasicCompactionEngine Compaction { get; }
        public CommandsService Commands { get; }
        public IDisposable CompactRegistration { get; }
        public MockSummaryAdapter Adapter { get; }

        public Harness(int contextWindow, string turnText, string summaryText = SummaryText, bool auto = true, int? tailTurns = null)
        {
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            Prompt = new SystemPrompt(Ctx, new SystemPromptConfig());
            Tools = new ToolRuntime(Ctx);
            Llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            _ = new AgentLoop(Ctx);
            Meter = new TokenMeter(Ctx);
            Pruner = new ToolResultPruner(Ctx);
            Compaction = new BasicCompactionEngine(Ctx, new BasicCompactionConfig { Auto = auto, TailTurns = tailTurns });
            Commands = CommandsService.Register(Ctx);
            CompactRegistration = CompactCommand.Register(Ctx);
            Adapter = new MockSummaryAdapter(contextWindow, turnText, summaryText);
            Llm.RegisterAdapter([Provider], Adapter);
        }

        public async Task<AgentLoopAgent> CreateAgent(string id)
        {
            var handle = await Agents.Create(new CreateAgentOptions(
                SessionId.Create(id),
                null,
                new AgentOptions(Provider, Model)));
            return (AgentLoopAgent)handle.Agent;
        }

        public void Dispose()
        {
            CompactRegistration.Dispose();
            Compaction.Dispose();
        }
    }

    private const int TurnContextWindow = 4000;

    private static readonly string[] TurnUsers =
    [
        new string('u', 14000),
        new string('o', 400),
        new string('p', 400),
        new string('q', 400),
    ];

    /**
     * N6 的保留轮数必须可以改: 默认 6, 且能通过 settings.yaml 的插件配置键覆盖(0 = 关闭降级)。
     * 绑定走 PluginConfigBinding 的键名忽略大小写匹配, 所以 yaml 里写 degradedRetainTurns。
     */
    [Fact]
    public void DegradedRetainTurns_DefaultsToSix_AndIsConfigurableFromSettings()
    {
        Assert.Equal(6, CompactionConfigResolver.ResolveConfig().DegradedRetainTurns);

        var bound = PluginConfigBinding.Bind<BasicCompactionConfig>(
            new Dictionary<string, object?> { ["degradedRetainTurns"] = 3L });

        Assert.Equal(3, CompactionConfigResolver.ResolveConfig(bound).DegradedRetainTurns);
        Assert.Equal(0, CompactionConfigResolver.ResolveConfig(
            new BasicCompactionConfig { DegradedRetainTurns = 0 }).DegradedRetainTurns);
    }

    private static async Task BuildFourTurns(AgentLoopAgent agent)
    {
        foreach (var user in TurnUsers)
        {
            agent.Followup(MessageFactory.CreateUserText(user));
            await agent.WhenIdle();
        }
    }

    /** 足够长的历史: 让"保留最近 N 轮后仍有旧历史可丢弃"成立(降级路径的前提)。 */
    private static async Task BuildTurns(AgentLoopAgent agent, int count)
    {
        for (var turn = 1; turn <= count; turn++)
        {
            agent.Followup(MessageFactory.CreateUserText($"turn {turn} 的内容 {new string('t', 200)}"));
            await agent.WhenIdle();
        }
    }

    private static List<long> UserSeqs(Session session)
        => session.SnapshotEvents()
            .Where(e => e.Data is UserMessagePayload { Message.Source: UserMessageSource })
            .Select(e => e.Seq)
            .ToList();

    private static string InstructionOf(GenerateOptions options)
        => Assert.IsType<TextBlock>(Assert.IsType<UserMessage>(options.Messages[^1]).Content[0]).Text;

    /**
     * 缓存命中的 token 已经算在 InputTokens 里: 再加一次会把占用虚高近一倍,
     * 让真实占用 40% 的会话被判成 80%、每一步都触发压缩(生产里就是这样卡死的)。
     */
    [Fact]
    public async Task Measurement_DoesNotCountCacheHitsTwice()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok");
        var agent = await harness.CreateAgent("compaction-cache-usage");
        // 真实占用 40 万: 其中 39.96 万命中缓存。
        harness.Adapter.Usage = new TokenUsage(400_252, 579, 400_831, CacheReadTokens: 399_616);

        agent.Followup(MessageFactory.CreateUserText(new string('u', 400)));
        await agent.WhenIdle();

        var measured = harness.Meter.Measure(agent.Session);
        // 缓存命中不得二次计入: TotalTokens 必须落在 provider 报的 input+output 附近, 而不是再加上增量。
        Assert.True(
            measured.TotalTokens is >= 400_000 and <= 401_000,
            $"measured {measured.TotalTokens} (baseline {measured.Baseline.Tokens}, surface {measured.SurfaceTokens})");
        Assert.DoesNotContain(agent.Session.SnapshotEvents(), sessionEvent => sessionEvent.Type == CompactionEventTypes.Start);
        Assert.Equal(0, harness.Adapter.CompactionCalls);
    }

    /**
     * 压力触发: 超过阈值后在下个回合边界压缩一次, 然后回到阈值以下。
     * 窗口 360 是刻意的: 触发检查时约 311 token ≥ 阈值 288, 压后约 263 < 288, 恰好一次就够。
     * 窗口再小(例如 280)会导致压完仍高于阈值, 引擎按 CompactionRetries 再压一次 ——
     * 那不是本用例要验证的路由。
     */
    [Fact]
    public async Task PressureTrigger_CompactsAtNextTurnBoundary()
    {
        using var harness = new Harness(contextWindow: 360, turnText: new string('x', 200));
        var agent = await harness.CreateAgent("compaction-pressure");
        agent.Followup(MessageFactory.CreateUserText(new string('u', 600)));
        await agent.WhenIdle();
        Assert.Equal(0, harness.Adapter.CompactionCalls);

        agent.Followup(MessageFactory.CreateUserText("second"));
        await agent.WhenIdle();

        Assert.Equal(1, harness.Adapter.CompactionCalls);
        var events = agent.Session.SnapshotEvents();
        var startPayload = events.Select(e => e.Data).OfType<CompactionStartPayload>().Single();
        var summaryPayload = events.Select(e => e.Data).OfType<CompactionSummaryPayload>().Single();
        var endPayload = events.Select(e => e.Data).OfType<CompactionEndPayload>().Single();
        Assert.Equal(startPayload.CompactionId, summaryPayload.CompactionId);
        Assert.Equal(startPayload.CompactionId, endPayload.CompactionId);
        Assert.Equal(2, startPayload.Turn);
        Assert.Equal(2, endPayload.Turn);
        Assert.Null(endPayload.Error);
        Assert.Null(startPayload.SourceCommandId);

        var userMessageSeqs = events
            .Where(e => e.Data is UserMessagePayload { Message.Source: UserMessageSource })
            .Select(e => e.Seq)
            .Take(1)
            .ToList();
        Assert.Equal(userMessageSeqs, summaryPayload.ShadowedSeqs);
        Assert.Equal(new ShadowedRange(userMessageSeqs[0], userMessageSeqs[^1]), summaryPayload.ShadowedRange);
        Assert.Equal(158, summaryPayload.ShadowedTokenCount);
        Assert.Equal(Provider, summaryPayload.Provider);
        Assert.Equal(Model, summaryPayload.Model);
        Assert.Equal(8192, summaryPayload.MaxTokens);
        Assert.True(summaryPayload.LlmStreamCall);
        Assert.Null(summaryPayload.Usage);
        Assert.Equal([new TextBlock(SummaryText)], summaryPayload.Summary);
        Assert.Equal(summaryPayload.Summary, summaryPayload.RawOutput);

        var startEvent = events.Single(e => e.Data is CompactionStartPayload);
        var summaryEvent = events.Single(e => e.Data is CompactionSummaryPayload);
        var endEvent = events.Single(e => e.Data is CompactionEndPayload);
        var checkpointEvent = events.Single(e =>
            e.Data is UserMessagePayload { Message.Source: PluginMessageSource { Plugin: CompactionCheckpoint.PluginName } });
        Assert.True(startEvent.Seq < summaryEvent.Seq);
        Assert.True(summaryEvent.Seq < checkpointEvent.Seq);
        Assert.True(checkpointEvent.Seq < endEvent.Seq);
        Assert.Equal(
            [startEvent.Seq, summaryEvent.Seq, .. summaryPayload.ShadowedSeqs],
            checkpointEvent.SourceEventSeqs);
        Assert.Equal(new SurfaceOp.Replace(userMessageSeqs[0], userMessageSeqs[^1]), checkpointEvent.SurfaceOp);

        var turnStart2 = events.Where(e => e.Data is TurnStartPayload { Turn: 2 }).Single();
        var stepStart2 = events.Where(e => e.Data is StepStartPayload { Turn: 2 }).Single();
        Assert.True(turnStart2.Seq < startEvent.Seq);
        Assert.True(endEvent.Seq < stepStart2.Seq);

        var messages = agent.Session.DeriveMessages();
        Assert.Equal(MessageRole.System, messages[0].Role);
        var checkpoint = Assert.IsType<UserMessage>(messages[1]);
        var source = Assert.IsType<PluginMessageSource>(checkpoint.Source);
        Assert.Equal(startPayload.CompactionId.Value, source.CompactionId);
        Assert.Null(source.SourceCommandId);
        Assert.StartsWith(Summarizer.CheckpointPreamble, Assert.IsType<TextBlock>(checkpoint.Content[0]).Text);
        Assert.Contains(Summarizer.SummaryOpenTag, Assert.IsType<TextBlock>(checkpoint.Content[0]).Text);
        Assert.Equal(SummaryText, Assert.IsType<TextBlock>(checkpoint.Content[1]).Text);
        Assert.Equal(Summarizer.SummaryCloseTag, Assert.IsType<TextBlock>(checkpoint.Content[^1]).Text);
        var retained = Assert.IsType<AssistantMessage>(messages[2]);
        Assert.Equal(new string('x', 200), Assert.IsType<TextBlock>(retained.Content[0]).Text);

        var summarization = harness.Adapter.LastCompactionOptions!;
        Assert.Equal(Provider, summarization.Provider);
        Assert.Equal(Model, summarization.Model);
        Assert.Equal(GeneratePurpose.Compaction, summarization.Purpose);
        Assert.Equal(8192, summarization.MaxTokens);
        Assert.Equal(agent.Session.Id, summarization.SessionId);
        var instruction = Assert.IsType<TextBlock>(Assert.IsType<UserMessage>(summarization.Messages[^1]).Content[0]);
        Assert.Equal(Summarizer.CompactionInstruction, instruction.Text);
        Assert.Equal(new string('u', 600), Assert.IsType<TextBlock>(summarization.Messages[0].Content[0]).Text);
    }

    [Fact]
    public async Task BelowThreshold_NoCompaction()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: new string('x', 200));
        var agent = await harness.CreateAgent("compaction-below-threshold");
        agent.Followup(MessageFactory.CreateUserText(new string('u', 400)));
        await agent.WhenIdle();
        agent.Followup(MessageFactory.CreateUserText("second"));
        await agent.WhenIdle();

        Assert.Equal(0, harness.Adapter.CompactionCalls);
        Assert.DoesNotContain(agent.Session.SnapshotEvents(), e => e.Type == CompactionEventTypes.Start);
    }

    [Fact]
    public void Pruner_PrunesOversizedToolResults()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok");
        var session = harness.Sessions.Create(SessionId.Create("prune-test"));
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("run")), new SurfaceOp.Append());
        var callId = ToolCallId.Create("call-1");
        var assistant = MessageFactory.CreateAssistantMessage([new ToolCallBlock(callId, "echo", "{}")], Provider, Model);
        session.Append(new AssistantMessagePayload(1, 1, assistant), new SurfaceOp.Append());
        var callSeq = session.Append(new ToolCallPayload(1, 1, callId, "echo", "{}")).Seq;
        var bigText = new string('a', 20000);
        var resultMessage = MessageFactory.CreateToolResultMessage(callId, [new TextBlock(bigText)], false);
        var resultSeq = session.Append(new ToolResultPayload(1, 1, resultMessage), new SurfaceOp.Append(), [callSeq]).Seq;
        session.Append(new StepEndPayload(1, 1));
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));

        var result = harness.Pruner.PruneSession(session);

        var entry = Assert.Single(result.Pruned);
        Assert.Equal(resultSeq, entry.OriginalSeq);
        Assert.Equal(callId, entry.CallId);
        Assert.Equal(20000, entry.CharsBefore);
        var expectedAfter = 4096 + ToolResultPruner.CodePointLength(ToolResultPruner.PruneMarker) + 1024;
        Assert.Equal(expectedAfter, entry.CharsAfter);
        Assert.Equal(20000 - expectedAfter, result.CharsRemoved);

        var pruneEvent = session.EventAt(entry.ReplacementSeq - 1)!;
        var prunePayload = Assert.IsType<CompactionPrunePayload>(pruneEvent.Data);
        Assert.Equal(new ShadowedRange(resultSeq, resultSeq), prunePayload.ShadowedRange);
        Assert.Equal([resultSeq], prunePayload.ShadowedSeqs);
        Assert.Equal(TokenEstimate.EstimateMessage(resultMessage), prunePayload.ShadowedTokenCount);

        var replacement = session.EventAt(entry.ReplacementSeq)!;
        Assert.Equal(SessionEventTypes.ToolResult, replacement.Type);
        Assert.Equal(new SurfaceOp.Replace(resultSeq, resultSeq), replacement.SurfaceOp);
        var replacementPayload = Assert.IsType<ToolResultPayload>(replacement.Data);
        var text = Assert.IsType<TextBlock>(replacementPayload.Message.Block.Content[0]).Text;
        Assert.Equal(expectedAfter, ToolResultPruner.CodePointLength(text));
        Assert.StartsWith(new string('a', 4096), text);
        Assert.EndsWith(new string('a', 1024), text);
        Assert.Contains(ToolResultPruner.PruneMarker, text);

        Assert.DoesNotContain(resultSeq, session.SurfaceManager.Nodes);
        Assert.Contains(entry.ReplacementSeq, session.SurfaceManager.Nodes);
        var derived = session.DeriveMessages();
        var toolResult = Assert.IsType<ToolResultMessage>(derived[^1]);
        Assert.Equal(text, Assert.IsType<TextBlock>(toolResult.Block.Content[0]).Text);

        var secondPass = harness.Pruner.PruneSession(session);
        Assert.Empty(secondPass.Pruned);
        Assert.Equal(0, secondPass.CharsRemoved);
    }

    [Fact]
    public async Task CompactCommand_RunsManualCompaction()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: new string('x', 200), auto: false);
        var agent = await harness.CreateAgent("compaction-command");
        await BuildFourTurns(agent);
        Assert.Equal(0, harness.Adapter.CompactionCalls);

        var execution = await harness.Commands.Execute(agent, "/compact", TestContext.Current.CancellationToken);

        Assert.NotNull(execution);
        var success = Assert.IsType<CommandResult.Success>(execution.Result);
        Assert.Equal(1, harness.Adapter.CompactionCalls);

        var events = agent.Session.SnapshotEvents();
        var startPayload = events.Select(e => e.Data).OfType<CompactionStartPayload>().Single();
        var endPayload = events.Select(e => e.Data).OfType<CompactionEndPayload>().Single();
        Assert.Null(startPayload.Turn);
        Assert.Null(endPayload.Turn);
        Assert.Equal(execution.CommandId, startPayload.SourceCommandId);
        Assert.Equal(execution.CommandId, endPayload.SourceCommandId);
        Assert.Null(endPayload.Error);
        var summaryEvent = events.Single(e => e.Data is CompactionSummaryPayload);
        Assert.Equal(summaryEvent.Seq, success.SourceEventSeq);

        // 手动 /compact 与自动一致:保留最后两个 user turn,只压缩更早的 turn。
        var summaryPayload = (CompactionSummaryPayload)summaryEvent.Data;
        var userSeqs = UserSeqs(agent.Session);
        Assert.Equal(4, userSeqs.Count);
        Assert.Equal(success.Text, $"Compacted {summaryPayload.ShadowedSeqs.Count} history items (~{summaryPayload.ShadowedTokenCount} tokens).");
        Assert.Equal(4, summaryPayload.ShadowedSeqs.Count);
        Assert.DoesNotContain(userSeqs[2], summaryPayload.ShadowedSeqs);
        Assert.DoesNotContain(userSeqs[3], summaryPayload.ShadowedSeqs);

        var types = events.Select(e => e.Type).ToList();
        var commandRun = types.IndexOf(CommandEvents.Run);
        var start = types.IndexOf(CompactionEventTypes.Start);
        var summary = types.IndexOf(CompactionEventTypes.Summary);
        var end = types.IndexOf(CompactionEventTypes.End);
        var commandDone = types.IndexOf(CommandEvents.Done);
        Assert.True(commandRun >= 0 && commandRun < start);
        Assert.True(start < summary);
        Assert.True(summary < end);
        Assert.True(end < commandDone);

        var messages = agent.Session.DeriveMessages();
        Assert.Equal(MessageRole.System, messages[0].Role);
        var checkpoint = Assert.IsType<UserMessage>(messages[1]);
        var source = Assert.IsType<PluginMessageSource>(checkpoint.Source);
        Assert.Equal(execution.CommandId, source.SourceCommandId);
        Assert.Equal(TurnUsers[2], Assert.IsType<TextBlock>(messages[2].Content[0]).Text);
        Assert.Equal(TurnUsers[3], Assert.IsType<TextBlock>(messages[4].Content[0]).Text);

        var json = JsonSerializer.Serialize(events, DshJson.Options);
        Assert.Contains("\"turn\":null", json);
        var restored = JsonSerializer.Deserialize<List<SessionEvent>>(json, DshJson.Options)!;
        Assert.Equal(events.Count, restored.Count);
        var restoredSummary = restored.Select(e => e.Data).OfType<CompactionSummaryPayload>().Single();
        var originalSummary = (CompactionSummaryPayload)summaryEvent.Data;
        Assert.Equal(originalSummary.CompactionId, restoredSummary.CompactionId);
        Assert.Equal(originalSummary.ShadowedRange, restoredSummary.ShadowedRange);
        Assert.Equal(originalSummary.ShadowedSeqs, restoredSummary.ShadowedSeqs);
        Assert.Equal(originalSummary.ShadowedTokenCount, restoredSummary.ShadowedTokenCount);
        Assert.Equal(originalSummary.Provider, restoredSummary.Provider);
        Assert.Equal(originalSummary.Model, restoredSummary.Model);
        Assert.Equal(originalSummary.MaxTokens, restoredSummary.MaxTokens);
        Assert.Equal(originalSummary.LlmStreamCall, restoredSummary.LlmStreamCall);
        Assert.Equal(
            originalSummary.Summary.OfType<TextBlock>().Select(block => block.Text),
            restoredSummary.Summary.OfType<TextBlock>().Select(block => block.Text));
        var restoredStart = restored.Select(e => e.Data).OfType<CompactionStartPayload>().Single();
        Assert.Equal(startPayload, restoredStart);
        var restoredEnd = restored.Select(e => e.Data).OfType<CompactionEndPayload>().Single();
        Assert.Equal(endPayload, restoredEnd);
        var restoredCheckpoint = restored
            .Select(e => e.Data)
            .OfType<UserMessagePayload>()
            .Single(payload => payload.Message.Source is PluginMessageSource { Plugin: CompactionCheckpoint.PluginName });
        var restoredSource = Assert.IsType<PluginMessageSource>(restoredCheckpoint.Message.Source);
        Assert.Equal(startPayload.CompactionId.Value, restoredSource.CompactionId);
        Assert.Equal(execution.CommandId, restoredSource.SourceCommandId);
    }

    [Fact]
    public async Task CompactCommand_EmptyHistoryAndPromptFollowup()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok");
        var agent = await harness.CreateAgent("compaction-command-edge");

        // 命令 + 提示词: compact 不拒绝多余文本, 其余文本是"压缩后继续"的提示词。
        var withPrompt = await harness.Commands.Execute(agent, "/compact now", TestContext.Current.CancellationToken);
        Assert.NotNull(withPrompt);
        var prompted = Assert.IsType<CommandResult.Success>(withPrompt.Result);
        Assert.Equal("No compactable history yet.", prompted.Text);
        Assert.Equal("now", prompted.FollowupPrompt);

        var empty = await harness.Commands.Execute(agent, "/compact", TestContext.Current.CancellationToken);
        Assert.NotNull(empty);
        var plain = Assert.IsType<CommandResult.Success>(empty.Result);
        Assert.Equal("No compactable history yet.", plain.Text);
        Assert.Null(plain.FollowupPrompt);
        Assert.Equal(0, harness.Adapter.CompactionCalls);
        Assert.DoesNotContain(agent.Session.SnapshotEvents(), e => e.Type == CompactionEventTypes.Start);
    }

    [Fact]
    public async Task CompactCommand_BusyWhileTurnRunning()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok");
        harness.Adapter.GateTurns = true;
        var agent = await harness.CreateAgent("compaction-command-busy");
        agent.Followup(MessageFactory.CreateUserText("hello"));
        await harness.Adapter.TurnStarted.Task;
        try
        {
            var execution = await harness.Commands.Execute(agent, "/compact", TestContext.Current.CancellationToken);
            Assert.NotNull(execution);
            var error = Assert.IsType<CommandResult.Error>(execution.Result).Text;
            Assert.Contains("between turns", error, StringComparison.Ordinal);
            Assert.Contains("still running", error, StringComparison.Ordinal);
        }
        finally
        {
            harness.Adapter.ReleaseTurn.TrySetResult();
        }
        await agent.WhenIdle();
    }

    [Fact]
    public async Task SelectCompactableRange_RespectsTailTurnsAndBudget()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: new string('x', 200), auto: false);
        var agent = await harness.CreateAgent("compaction-select");
        await BuildFourTurns(agent);
        var session = agent.Session;
        var userSeqs = UserSeqs(session);

        var byTwoTurns = CompactionRegion.SelectCompactableRange(session, harness.Meter.Measure(session), retainTokens: 640, tailTurns: 2);
        Assert.NotNull(byTwoTurns);
        Assert.Equal(userSeqs[0], byTwoTurns.Start);
        Assert.True(byTwoTurns.End < userSeqs[2]);

        var byOneTurn = CompactionRegion.SelectCompactableRange(session, harness.Meter.Measure(session), retainTokens: 640, tailTurns: 1);
        Assert.NotNull(byOneTurn);
        Assert.Equal(userSeqs[0], byOneTurn.Start);
        Assert.True(byOneTurn.End >= userSeqs[2] && byOneTurn.End < userSeqs[3]);

        var budgetTooSmall = CompactionRegion.SelectCompactableRange(session, harness.Meter.Measure(session), retainTokens: 1, tailTurns: 1);
        Assert.NotNull(budgetTooSmall);
        Assert.Equal(userSeqs[3], budgetTooSmall.End);

        var generousBudget = CompactionRegion.SelectCompactableRange(session, harness.Meter.Measure(session), retainTokens: 100_000, tailTurns: 4);
        Assert.True(generousBudget is null || generousBudget.End < userSeqs[0]);
    }

    /**
     * F1: 摘要彻底失败(这里是 API 报错)时不能把整次压缩判死。
     * 降级策略=丢弃旧历史 + 保留最近 N 轮原文, 这样"压不动"不再等于"会话变一次性"。
     */
    [Fact]
    public async Task ManualCompaction_DegradesInsteadOfFailing_WhenSummaryFails()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-degrade");
        await BuildTurns(agent, 10);
        harness.Adapter.CompactionError = _ => new LlmException(new LlmFailure("upstream exploded", "ERROR"));

        var result = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        // 降级写回的摘要必须说明"历史被丢弃", 而不是伪装成摘要成功。
        var summaryText = MessageText.Flatten(result.Summary);
        Assert.Contains("history discarded", summaryText, StringComparison.OrdinalIgnoreCase);
        // 占位摘要不是 LLM 产出, 事件里要如实标记。
        var summaryPayload = agent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<CompactionSummaryPayload>()
            .Last();
        Assert.False(summaryPayload.LlmStreamCall ?? true);
        // 最近几轮原文必须还在表面上(降级只丢旧历史)。
        var surfaceUserMessages = agent.Session.SurfaceManager.Nodes
            .Select(seq => agent.Session.EventAt(seq)?.Data)
            .OfType<UserMessagePayload>()
            .Count(payload => payload.Message.Source is UserMessageSource);
        Assert.True(surfaceUserMessages > 0, "degraded compaction must keep the recent user turns");
    }

    /** N6: 摘要比被压缩内容还大时同样走降级, 不硬失败。 */
    [Fact]
    public async Task ManualCompaction_DegradesWhenSummaryIsLargerThanShadowedContent()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-degrade-bigger");
        await BuildTurns(agent, 10);
        // 摘要比被 shadow 的历史还长, 触发"压缩后更大"的守卫。
        harness.Adapter.SummaryOverride = new string('s', 40_000);

        var result = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("history discarded", MessageText.Flatten(result.Summary), StringComparison.OrdinalIgnoreCase);
    }

    /**
     * 真实形状: 压缩会在表面中间插入 checkpoint(带新的 seq), 表面上还有 7 条用户消息时,
     * "保留最近 6 轮"剩下的可丢区间只有 checkpoint 加一个节点, 比占位摘要还小。
     * 降级必须继续退到更少的保留轮数, 否则它会一直返回 null, 会话就永远压不动。
     */
    [Fact]
    public async Task DegradedCompaction_StepsDownPastCandidatesThatDiscardTooLittle()
    {
        using var harness = new Harness(contextWindow: 280, turnText: new string('x', 200), auto: false);
        var agent = await harness.CreateAgent("compaction-degrade-stepdown");
        await BuildTurns(agent, 8);
        // 先做一次真实压缩, 让表面中间出现 checkpoint(新 seq 插在旧 seq 之前的位置)。
        var first = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);
        Assert.NotNull(first);
        // 压缩后继续长内容, 让表面重新越过阈值(此时 checkpoint 已插在表面中间)。
        await BuildTurns(agent, 4);
        // 之后摘要彻底不可用: 只剩降级这一条路。
        harness.Adapter.CompactionError = _ => new LlmException(new LlmFailure("upstream exploded", "ERROR"));
        var before = harness.Meter.Measure(agent.Session).TotalTokens;

        agent.Session.Append(new TurnStartPayload(99));
        await CompactIgnoringOverflowAsync(harness, agent);

        var discarded = agent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<CompactionSummaryPayload>()
            .Last();
        Assert.False(discarded.LlmStreamCall ?? true);
        Assert.Contains("history discarded", MessageText.Flatten(discarded.Summary), StringComparison.OrdinalIgnoreCase);
        Assert.True(harness.Meter.Measure(agent.Session).TotalTokens < before);
    }

    /**
     * 自动(压力)压缩遇到"摘要一个字正文都没产出"时必须降级, 而不是报错收场。
     * 生产里正是这样卡死的: 上下文一直高于阈值, 每一步都重跑一遍注定失败的摘要。
     */
    [Fact]
    public async Task AutoCompaction_DegradesInsteadOfFailing_WhenSummaryProducesNoText()
    {
        using var harness = new Harness(contextWindow: 280, turnText: new string('x', 200), auto: false);
        var agent = await harness.CreateAgent("compaction-auto-degrade");
        await BuildTurns(agent, 8);
        // 摘要请求返回空文本 = 生产里的 "summarization produced no text summary content"。
        harness.Adapter.SummaryOverride = "";
        agent.Session.Append(new TurnStartPayload(9));

        var before = harness.Meter.Measure(agent.Session).TotalTokens;
        await CompactIgnoringOverflowAsync(harness, agent);

        var summaries = agent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<CompactionSummaryPayload>()
            .ToList();
        Assert.NotEmpty(summaries);
        Assert.All(summaries, summary => Assert.False(summary.LlmStreamCall ?? true));
        var degraded = summaries[^1];
        Assert.Contains("history discarded", MessageText.Flatten(degraded.Summary), StringComparison.OrdinalIgnoreCase);
        // 关键是历史真的变短了: 否则压缩等于没做, 下一步还会再压一次。
        Assert.True(harness.Meter.Measure(agent.Session).TotalTokens < before);
    }

    /**
     * 表面只剩不到 degradedRetainTurns 轮时, 降级仍必须腾出空间:
     * 按 6 轮找不到选区就放弃的话, 刚压缩过的会话会永远"压不动", 而它已经高于阈值。
     */
    [Fact]
    public async Task DegradedCompaction_FreesSpace_WhenFewerTurnsThanRetained()
    {
        using var harness = new Harness(contextWindow: 280, turnText: new string('x', 200), auto: false);
        var agent = await harness.CreateAgent("compaction-degrade-few-turns");
        await BuildTurns(agent, 4);
        harness.Adapter.CompactionError = _ => new LlmException(new LlmFailure("upstream exploded", "ERROR"));
        agent.Session.Append(new TurnStartPayload(5));

        var before = harness.Meter.Measure(agent.Session).TotalTokens;
        await CompactIgnoringOverflowAsync(harness, agent);

        var discarded = agent.Session.SnapshotEvents()
            .Select(sessionEvent => sessionEvent.Data)
            .OfType<CompactionSummaryPayload>()
            .Last();
        Assert.Contains("history discarded", MessageText.Flatten(discarded.Summary), StringComparison.OrdinalIgnoreCase);
        // 降级确实丢了旧历史: 表面上不再有第一轮的用户消息。
        var firstUserSeq = UserSeqs(agent.Session)[0];
        Assert.DoesNotContain(firstUserSeq, agent.Session.SurfaceManager.Nodes);
        Assert.True(harness.Meter.Measure(agent.Session).TotalTokens < before);
    }

    /**
     * 自动压缩在"保留轮本身就超过阈值"时仍会以"仍高于阈值"收场——那是压不动的客观事实, 由调用方记录后继续回合。
     * 这里只关心它有没有白跑: 抛出的必须不是原始的摘要失败。
     */
    private static async Task CompactIgnoringOverflowAsync(Harness harness, AgentLoopAgent agent)
    {
        try
        {
            await harness.Compaction.CompactIfNeeded(agent, CompactionTrigger.Pressure, TestContext.Current.CancellationToken);
        }
        catch (InvalidOperationException error) when (error.Message.Contains("still above threshold", StringComparison.Ordinal))
        {
        }
    }

    /**
     * 失败的自动压缩不得在"表面没变"时重跑: 一次失败的分块摘要要几分钟,
     * 每一步重试一次会让界面看起来完全没反应(用户看到的就是"GUI 死了")。
     */
    [Fact]
    public async Task FailedPressureCompaction_IsNotRetriedWhileTheSurfaceIsUnchanged()
    {
        using var harness = new Harness(contextWindow: 280, turnText: new string('x', 200), auto: false);
        var agent = await harness.CreateAgent("compaction-latch");
        agent.Followup(MessageFactory.CreateUserText(new string('u', 600)));
        await agent.WhenIdle();
        agent.Session.Append(new TurnStartPayload(2));
        harness.Adapter.CompactionError = _ => new LlmException(new LlmFailure("upstream exploded", "ERROR"));

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Compaction.CompactIfNeeded(
            agent, CompactionTrigger.Pressure, TestContext.Current.CancellationToken));
        var callsAfterFailure = harness.Adapter.CompactionCalls;

        var retried = await harness.Compaction.CompactIfNeeded(
            agent, CompactionTrigger.Pressure, TestContext.Current.CancellationToken);

        Assert.Null(retried);
        Assert.Equal(callsAfterFailure, harness.Adapter.CompactionCalls);
        // 表面长出新内容之后才值得再试一次。
        agent.Session.Append(new TurnStartPayload(3));
        agent.Session.Append(new StepStartPayload(3, 1));
        agent.Session.Append(new UserMessagePayload(MessageFactory.CreateUserText(new string('v', 4_000))), new SurfaceOp.Append());
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Compaction.CompactIfNeeded(
            agent, CompactionTrigger.Pressure, TestContext.Current.CancellationToken));
        Assert.True(harness.Adapter.CompactionCalls > callsAfterFailure);
    }

    /**
     * 摘要请求取模型最省推理的一档: 推理模型会把 maxTokens 全烧在思维链上, 正文一个字都不剩,
     * 那正是生产里的 "summarization produced no text summary content"。
     */
    [Fact]
    public async Task SummaryRequest_AsksForTheCheapestReasoningEffort()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: "ok", auto: false);
        harness.Adapter.Reasoning = new LlmModelReasoningInfo(
            [
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("off"), "Off"),
                new LlmReasoningEffortInfo(ReasoningEffortId.Create("high"), "High"),
            ],
            ReasoningEffortId.Create("high"));
        var agent = await harness.CreateAgent("compaction-reasoning");
        await BuildFourTurns(agent);

        _ = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);

        Assert.Equal("off", harness.Adapter.LastCompactionOptions!.ReasoningEffort?.Value);
    }

    /** 模型不提供 "off" 时不替它猜档位: 摘要请求保持默认, 由模型自己决定。 */
    [Fact]
    public async Task SummaryRequest_KeepsDefaultEffort_WhenNoOffIsOffered()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: "ok", auto: false);
        harness.Adapter.Reasoning = new LlmModelReasoningInfo(
            [new LlmReasoningEffortInfo(ReasoningEffortId.Create("high"), "High")],
            ReasoningEffortId.Create("high"));
        var agent = await harness.CreateAgent("compaction-reasoning-default");
        await BuildFourTurns(agent);

        _ = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);

        Assert.Null(harness.Adapter.LastCompactionOptions!.ReasoningEffort);
    }

    /** F2: 摘要请求不带工具 schema —— 提示词已禁止调用工具, 带 schema 只会挤占摘要预算。 */
    [Fact]
    public async Task SummaryRequest_OmitsToolSchemas()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-no-tools");
        await BuildFourTurns(agent);

        _ = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);

        var options = Assert.IsType<GenerateOptions>(harness.Adapter.LastCompactionOptions);
        Assert.Null(options.Tools);
    }

    /** N2: 摘要被 maxTokens 截断时接受已产出的内容, 而不是整批失败。 */
    [Fact]
    public async Task TruncatedSummary_IsAcceptedInsteadOfFailing()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-truncated");
        await BuildFourTurns(agent);
        harness.Adapter.CompactionFinish = _ => new FinishReason.MaxTokens();

        var result = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains(SummaryText, MessageText.Flatten(result.Summary), StringComparison.Ordinal);
    }

    /** N1: 手动压缩也要先剪枝(自动路径一直这么做, 手动路径原先漏了)。 */
    [Fact]
    public async Task ManualCompaction_PrunesOversizedToolResultsFirst()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-manual-prune");
        var session = agent.Session;
        var callId = ToolCallId.Create("call-huge");
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText(new string('u', 6_000))), new SurfaceOp.Append());
        var assistant = MessageFactory.CreateAssistantMessage([new ToolCallBlock(callId, "bash", "{}")], Provider, Model);
        session.Append(new AssistantMessagePayload(1, 1, assistant), new SurfaceOp.Append());
        var callSeq = session.Append(new ToolCallPayload(1, 1, callId, "bash", "{}")).Seq;
        var huge = MessageFactory.CreateToolResultMessage(callId, [new TextBlock(new string('a', 30_000))], false);
        session.Append(new ToolResultPayload(1, 1, huge), new SurfaceOp.Append(), [callSeq]);
        session.Append(new StepEndPayload(1, 1));
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        foreach (var text in new[] { "turn two", "turn three", "turn four" })
        {
            agent.Followup(MessageFactory.CreateUserText(text));
            await agent.WhenIdle();
        }

        _ = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);

        Assert.Contains(agent.Session.SnapshotEvents(), sessionEvent => sessionEvent.Type == CompactionEventTypes.Prune);
    }

    /**
     * 上一个进程起了压缩但没等到结束就退出时, 会话里会留下没有配对的 compaction/start。
     * 这种标记必须能自愈, 否则该会话此后每次压缩都报"已有压缩在进行", 永久压不动。
     */
    [Fact]
    public async Task InheritedCompactionMarker_IsClosedSoTheSessionStaysCompactable()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-stale-marker");
        await BuildTurns(agent, 10);
        var started = agent.Session.Append(new CompactionStartPayload(CompactionId.Create("stale"), null, null));

        // 重启后的会话: 之前的全部事件(含未闭合的 start)都是"从磁盘继承"的内容。
        var resumed = Session.Create(agent.Session.Id, agent.Session.SnapshotEvents());
        Assert.True(started.Seq < resumed.FirstLiveSeq);

        var closed = CompactionRegion.CloseInheritedCompactionMarker(resumed);

        Assert.NotNull(closed);
        var end = Assert.IsType<CompactionEndPayload>(closed.Data);
        Assert.Contains("interrupted", end.Error!, StringComparison.OrdinalIgnoreCase);
        // 幂等: 再调一次没有可关的标记。
        Assert.Null(CompactionRegion.CloseInheritedCompactionMarker(resumed));
    }

    /** 本进程里真的有一次压缩在跑时不能被当成陈旧标记关掉。 */
    [Fact]
    public async Task LiveCompactionMarker_IsNotClosedAsStale()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-live-marker");
        await BuildTurns(agent, 10);
        agent.Session.Append(new CompactionStartPayload(CompactionId.Create("live"), null, null));

        Assert.Null(CompactionRegion.CloseInheritedCompactionMarker(agent.Session));
    }

    /** 再压一次时, 错误信息要说明"上一次压缩从什么时候开始、已经跑了多久"。 */
    [Fact]
    public async Task SecondCompaction_ReportsTheActiveCompactionAndItsStartTime()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-second-attempt");
        await BuildTurns(agent, 10);
        agent.Session.Append(new CompactionStartPayload(CompactionId.Create("live"), null, null));

        var execution = await harness.Commands.Execute(agent, "/compact", TestContext.Current.CancellationToken);

        var error = Assert.IsType<CommandResult.Error>(Assert.IsType<CommandExecution>(execution).Result);
        Assert.Contains("already running", error.Text, StringComparison.Ordinal);
        Assert.Contains("started ", error.Text, StringComparison.Ordinal);
        Assert.Contains("Esc", error.Text, StringComparison.Ordinal);
    }

    /** "回合还没结束"与"压缩正在进行"不是一回事: 前者要用户等/停掉当前回合。 */
    [Fact]
    public async Task CompactionDuringOpenTurn_SaysTheTurnMustFinishFirst()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-open-turn");
        await BuildTurns(agent, 10);
        agent.Session.Append(new TurnStartPayload(11));

        var execution = await harness.Commands.Execute(agent, "/compact", TestContext.Current.CancellationToken);

        var error = Assert.IsType<CommandResult.Error>(Assert.IsType<CommandExecution>(execution).Result);
        Assert.Contains("between turns", error.Text, StringComparison.Ordinal);
        Assert.Contains("turn 11", error.Text, StringComparison.Ordinal);
    }

    /**
     * 压缩请求的消息序列必须对 OpenAI 兼容 API 合法: 带 tool_calls 的 assistant 后面必须有对应 tool 消息。
     * 会话是按事件配对的, 直接派生成消息可能切坏配对, 服务端会回 HTTP 400 invalid_request_error。
     * 这里端到端确认发给模型的 messages 里不再有裸工具结构, 且工具输出仍以文本保留。
     */
    [Fact]
    public async Task ManualCompaction_SendsApiLegalMessages_ForToolCallHistory()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok", auto: false);
        var agent = await harness.CreateAgent("compaction-tool-pairing");
        var session = agent.Session;
        var callId = ToolCallId.Create("call-turn");
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText(new string('q', 3_000))), new SurfaceOp.Append());
        var assistant = MessageFactory.CreateAssistantMessage(
            [new ToolCallBlock(callId, "bash", """{"command":"dotnet test"}""")], Provider, Model);
        session.Append(new AssistantMessagePayload(1, 1, assistant), new SurfaceOp.Append());
        var callSeq = session.Append(new ToolCallPayload(1, 1, callId, "bash", """{"command":"dotnet test"}""")).Seq;
        var result = MessageFactory.CreateToolResultMessage(callId, [new TextBlock("2 tests failed")], true);
        session.Append(new ToolResultPayload(1, 1, result), new SurfaceOp.Append(), [callSeq]);
        session.Append(new StepEndPayload(1, 1));
        session.Append(new TurnEndPayload(1, new TurnEndReason.Completed()));
        session.Append(new TurnStartPayload(2));
        session.Append(new StepStartPayload(2, 1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("继续")), new SurfaceOp.Append());
        session.Append(new StepEndPayload(2, 1));
        session.Append(new TurnEndPayload(2, new TurnEndReason.Completed()));
        // 补足历史: 默认只保留最近 2 轮, 需要更早的轮次落入被压缩区间(带工具的那一轮必须在区间内)。
        for (var turn = 3; turn <= 6; turn++)
        {
            agent.Followup(MessageFactory.CreateUserText($"第 {turn} 轮 {new string('w', 3_000)}"));
            await agent.WhenIdle();
        }

        var compaction = await harness.Compaction.CompactNow(agent, TestContext.Current.CancellationToken);

        Assert.NotNull(compaction);
        var sent = harness.Adapter.LastCompactionOptions!.Messages;
        Assert.DoesNotContain(sent, message => message.Content.Any(block => block is ToolCallBlock));
        Assert.DoesNotContain(sent, message => message is ToolResultMessage);
        Assert.DoesNotContain(sent, message => message.Content.Any(block => block is ToolResultBlock));
        // 拍平不等于丢信息: 工具名、参数与失败输出仍要出现在摘要输入里。
        var text = MessageText.Flatten([.. sent.SelectMany(message => message.Content)]);
        Assert.Contains("bash", text, StringComparison.Ordinal);
        Assert.Contains("dotnet test", text, StringComparison.Ordinal);
        Assert.Contains("2 tests failed", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManualAndAutomatic_SelectSameTailTurns()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: new string('x', 200), auto: false);

        var manualAgent = await harness.CreateAgent("compaction-manual-consistency");
        await BuildFourTurns(manualAgent);
        var manual = await harness.Compaction.CompactNow(manualAgent, TestContext.Current.CancellationToken);

        var autoAgent = await harness.CreateAgent("compaction-auto-consistency");
        await BuildFourTurns(autoAgent);
        autoAgent.Session.Append(new TurnStartPayload(5));
        var auto = await harness.Compaction.CompactIfNeeded(autoAgent, CompactionTrigger.Pressure, TestContext.Current.CancellationToken);

        Assert.NotNull(manual);
        Assert.NotNull(auto);
        Assert.Equal(manual.ShadowedSeqs, auto.ShadowedSeqs);
        Assert.Equal(manual.ShadowedTokenCount, auto.ShadowedTokenCount);
        Assert.Equal(4, manual.ShadowedSeqs.Count);
        var retained = UserSeqs(manualAgent.Session);
        Assert.DoesNotContain(retained[2], manual.ShadowedSeqs);
        Assert.DoesNotContain(retained[3], manual.ShadowedSeqs);
    }

    [Fact]
    public async Task CompactTool_RegistersAndCompactsWithFocus()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: new string('x', 200), auto: false);
        var agent = await harness.CreateAgent("compaction-tool");
        await BuildFourTurns(agent);
        using var compactTool = CompactTool.Register(harness.Ctx);

        var definition = harness.Tools.Get(CompactTool.ToolName);
        Assert.NotNull(definition);
        Assert.True(definition.Parameters["properties"]!.AsObject().ContainsKey("focus"));
        Assert.Contains("focus", CompactTool.SectionText);

        // 运行时确认注册点:工具 schema 与提示词段都在装配结果里。
        var assembly = await harness.Prompt.Assemble(new AssembleContext());
        Assert.Contains(assembly.Sections, section => section.Name == "tool:compact" && section.Text == CompactTool.SectionText);
        Assert.Contains(assembly.Tools, tool => tool.Name == CompactTool.ToolName);

        agent.Session.Append(new TurnStartPayload(5));
        agent.Session.Append(new StepStartPayload(5, 1));
        var focus = "keep the failing test and the agreed API contract";
        var execution = await harness.Tools.Execute(new ToolExecutionInput
        {
            CallId = ToolCallId.Create("call-compact"),
            Name = CompactTool.ToolName,
            Arguments = JsonSerializer.SerializeToElement(new { focus }),
            Signal = TestContext.Current.CancellationToken,
            Agent = agent,
        });

        var success = Assert.IsType<ToolExecutionResult.Success>(execution);
        Assert.True(success.Value.GetProperty("compacted").GetBoolean());
        Assert.Equal(4, success.Value.GetProperty("shadowedItems").GetInt32());
        var instruction = InstructionOf(harness.Adapter.LastCompactionOptions!);
        Assert.Contains(Summarizer.CompactionInstruction, instruction);
        Assert.Contains(focus, instruction);
    }

    /**
     * 分块预算按模型上下文推算: 块越大, 请求数越少, 压缩越快。
     * 这正是"压缩跑了 16 分钟"的修正点 —— 旧实现按固定 16k 字符切块, 一次几十 MB 的会话会被切成上百块。
     */
    [Fact]
    public void ChunkBudget_FollowsTheModelContextWindow()
    {
        using var big = new Harness(contextWindow: 1_000_000, turnText: "ok");
        using var tiny = new Harness(contextWindow: 1_000, turnText: "ok");

        var budget = CompactionSummarizer.ChunkBudgetTokens(big.Llm, Provider, Model, 8192);

        // 可用上下文 = 窗口 - 输出预留, 再取 60%。
        Assert.Equal((int)Math.Floor((1_000_000 - 8192) * CompactionSummarizer.ChunkBudgetRatio), budget);
        Assert.True(budget > 500_000, $"a 1M-token model must accept very large chunks, got {budget}");
        // 窗口小于输出预留时收敛到下限, 不能算出 0 或负数(那会让分块退化成每条消息一块)。
        Assert.Equal(CompactionSummarizer.MinChunkTokens, CompactionSummarizer.ChunkBudgetTokens(tiny.Llm, Provider, Model, 8192));
    }

    /** 模型窗口未知时不按大窗口切块(那会被服务端拒), 退到保守预算。 */
    [Fact]
    public void ChunkBudget_FallsBackWhenTheContextWindowIsUnknown()
    {
        using var harness = new Harness(contextWindow: 0, turnText: "ok");

        Assert.Equal(
            CompactionSummarizer.UnknownContextChunkTokens,
            CompactionSummarizer.ChunkBudgetTokens(harness.Llm, Provider, Model, 8192));
    }

    /**
     * 回归: 大窗口模型下的中长历史必须一次请求压完。
     * 旧实现把 300k 字符切成 19 块, 每块都要生成一段摘要, 输出生成的时间叠起来就是十几分钟。
     */
    [Fact]
    public async Task LargeTranscriptOnBigWindow_IsSummarizedInOneRequest()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok");
        var agent = await harness.CreateAgent("compaction-single-request");
        var input = new SummarizationInput(null, null,
            [.. Enumerable.Range(0, 30).Select(index => MessageFactory.CreateUserText(new string((char)('a' + index % 26), 10_000)))]);

        var result = await CompactionSummarizer.Summarize(
            harness.Llm, Provider, Model, 8192, input, agent, TestContext.Current.CancellationToken);

        Assert.Equal(1, harness.Adapter.CompactionCalls);
        Assert.Equal(SummaryText, string.Concat(result.Summary.OfType<TextBlock>().Select(block => block.Text)));
    }

    /**
     * 工具结果在**第一次**请求里就要被裁剪。旧实现只在服务端报 payload 过大时才裁,
     * 于是首轮请求带着全部工具输出, 既慢又容易超限。会话里工具结果常占一半以上字符。
     */
    [Fact]
    public async Task Slimming_AppliesToTheFirstRequest()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok");
        var agent = await harness.CreateAgent("compaction-slim-first");
        var callId = ToolCallId.Create("call-huge");
        var huge = new string('a', 50_000);
        var input = new SummarizationInput(null, null,
        [
            MessageFactory.CreateUserMessage(
                [new TextBlock("run it"), new ToolResultBlock(callId, [new TextBlock(huge)], false)]),
        ]);

        _ = await CompactionSummarizer.Summarize(
            harness.Llm, Provider, Model, 8192, input, agent, TestContext.Current.CancellationToken);

        var first = MessageText.Flatten(harness.Adapter.CompactionOptions[0].Messages[0].Content);
        Assert.Contains("[truncated for compaction]", first, StringComparison.Ordinal);
        Assert.True(
            first.Length < CompactionSummarizer.ToolResultMaxChars * 2,
            $"first request still carries the oversized tool result ({first.Length} chars)");
    }

    [Fact]
    public async Task ChunkedSummarization_SplitsAndMerges()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: "ok");
        var agent = await harness.CreateAgent("compaction-chunks");
        var input = new SummarizationInput(null, null,
        [
            MessageFactory.CreateUserText(new string('a', 20_000)),
            MessageFactory.CreateUserText(new string('b', 20_000)),
            MessageFactory.CreateUserText(new string('c', 20_000)),
        ]);

        var result = await CompactionSummarizer.Summarize(
            harness.Llm, Provider, Model, 8192, input, agent, TestContext.Current.CancellationToken);

        // 小块预算下必然分多次请求: 首段与中段是分块指令, 最后一次是合并指令。
        Assert.True(harness.Adapter.CompactionCalls > 1, $"expected chunked summarization, got {harness.Adapter.CompactionCalls} call(s)");
        Assert.Equal(SummaryText, string.Concat(result.Summary.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Equal(CompactionSummarizer.ChunkInstruction, InstructionOf(harness.Adapter.CompactionOptions[0]));
        Assert.Equal(CompactionSummarizer.MergeInstruction, InstructionOf(harness.Adapter.CompactionOptions[^1]));
    }

    /**
     * 服务端报 payload 过大时, 缩小预算重新切块 —— 内容在入口已经裁剪过, 原样重发没有意义。
     * 这里构造"单次能过预算、但服务端拒收"的输入, 确认它被切成多块而不是直接失败。
     */
    [Fact]
    public async Task PayloadTooLarge_RetriesWithSmallerChunksInsteadOfResending()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok");
        var agent = await harness.CreateAgent("compaction-payload-shrink");
        var input = new SummarizationInput(null, null,
            [.. Enumerable.Range(0, 80).Select(_ => MessageFactory.CreateUserText(new string('a', 10_000)))]);
        harness.Adapter.CompactionError = index => index == 0
            ? new LlmException(new LlmFailure("Request entity too large", "INVALID_REQUEST"))
            : null;

        var result = await CompactionSummarizer.Summarize(
            harness.Llm, Provider, Model, 8192, input, agent, TestContext.Current.CancellationToken);

        Assert.True(harness.Adapter.CompactionCalls > 2, "expected the retry to be re-chunked, got a single resend");
        Assert.Equal(SummaryText, string.Concat(result.Summary.OfType<TextBlock>().Select(block => block.Text)));
        Assert.Equal(CompactionSummarizer.MergeInstruction, InstructionOf(harness.Adapter.CompactionOptions[^1]));
        // 重试那一次只带一块, 而不是又发整段历史。
        var retried = harness.Adapter.CompactionOptions[1].Messages.Count;
        Assert.True(retried < harness.Adapter.CompactionOptions[0].Messages.Count, "retry must carry a smaller slice");
    }

    /** 无法再切分的输入(单条消息)在服务端拒收后直接失败, 不无限下钻。 */
    [Fact]
    public async Task PayloadTooLarge_NonSplittableMessageFailsWithoutRecursing()
    {
        using var harness = new Harness(contextWindow: TurnContextWindow, turnText: "ok");
        var agent = await harness.CreateAgent("compaction-payload-guard");
        var input = new SummarizationInput(null, null, [MessageFactory.CreateUserText(new string('z', 20_000))]);
        harness.Adapter.CompactionError = _ => new LlmException(new LlmFailure("Request entity too large", "INVALID_REQUEST"));

        await Assert.ThrowsAnyAsync<HarnessException>(() => CompactionSummarizer.Summarize(
            harness.Llm, Provider, Model, 8192, input, agent, TestContext.Current.CancellationToken));

        Assert.Equal(1, harness.Adapter.CompactionCalls);
    }
}
