using System.Runtime.CompilerServices;
using System.Text.Json;
using Cordis;
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

        public TaskCompletionSource TurnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseTurn { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool GateTurns { get; set; }

        public override LlmProviderInfo ProviderInfo => new(Provider, "Test Provider");

        public override ResolvedRetryPolicy ProviderRetryPolicy => ResolvedRetryPolicy.Resolve(null, "test");

        public override LlmResolvedModelInfo? ResolveModel(string model)
            => new(Provider, model, model, ContextWindow: _contextWindow);

        public override async IAsyncEnumerable<StreamChunk> Stream(
            GenerateOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            string text;
            if (options.Purpose == GeneratePurpose.Compaction)
            {
                CompactionCalls++;
                LastCompactionOptions = options;
                text = _summaryText;
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
            yield return new StreamChunk.Finish(new FinishReason.Stop());
        }
    }

    private sealed class Harness : IDisposable
    {
        public Context Ctx { get; }
        public SessionStore Sessions { get; }
        public LlmRuntime Llm { get; }
        public AgentRegistry Agents { get; }
        public ToolResultPruner Pruner { get; }
        public BasicCompactionEngine Compaction { get; }
        public CommandsService Commands { get; }
        public IDisposable CompactRegistration { get; }
        public MockSummaryAdapter Adapter { get; }

        public Harness(int contextWindow, string turnText, string summaryText = SummaryText)
        {
            Ctx = new Context();
            Sessions = new SessionStore(Ctx);
            _ = new SystemPrompt(Ctx, new SystemPromptConfig());
            _ = new ToolRuntime(Ctx);
            Llm = new LlmRuntime(Ctx);
            Agents = new AgentRegistry(Ctx);
            _ = new AgentLoop(Ctx);
            _ = new TokenMeter(Ctx);
            Pruner = new ToolResultPruner(Ctx);
            Compaction = new BasicCompactionEngine(Ctx);
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

    [Fact]
    public async Task PressureTrigger_CompactsAtNextTurnBoundary()
    {
        using var harness = new Harness(contextWindow: 280, turnText: new string('x', 200));
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
        var checkpoint = Assert.IsType<UserMessage>(messages[0]);
        var source = Assert.IsType<PluginMessageSource>(checkpoint.Source);
        Assert.Equal(startPayload.CompactionId.Value, source.CompactionId);
        Assert.Null(source.SourceCommandId);
        Assert.StartsWith(Summarizer.CheckpointPreamble, Assert.IsType<TextBlock>(checkpoint.Content[0]).Text);
        Assert.Contains(Summarizer.SummaryOpenTag, Assert.IsType<TextBlock>(checkpoint.Content[0]).Text);
        Assert.Equal(SummaryText, Assert.IsType<TextBlock>(checkpoint.Content[1]).Text);
        Assert.Equal(Summarizer.SummaryCloseTag, Assert.IsType<TextBlock>(checkpoint.Content[^1]).Text);
        var retained = Assert.IsType<AssistantMessage>(messages[1]);
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
        using var harness = new Harness(contextWindow: 1_000_000, turnText: new string('x', 200));
        var agent = await harness.CreateAgent("compaction-command");
        agent.Followup(MessageFactory.CreateUserText(new string('u', 600)));
        await agent.WhenIdle();
        Assert.Equal(0, harness.Adapter.CompactionCalls);

        var execution = await harness.Commands.Execute(agent, "/compact");

        Assert.NotNull(execution);
        var success = Assert.IsType<CommandResult.Success>(execution.Result);
        Assert.Equal("Compacted 1 history items (~158 tokens).", success.Text);
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
        var checkpoint = Assert.IsType<UserMessage>(messages[0]);
        var source = Assert.IsType<PluginMessageSource>(checkpoint.Source);
        Assert.Equal(execution.CommandId, source.SourceCommandId);

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
    public async Task CompactCommand_UsageAndEmptyHistory()
    {
        using var harness = new Harness(contextWindow: 1_000_000, turnText: "ok");
        var agent = await harness.CreateAgent("compaction-command-edge");

        var usage = await harness.Commands.Execute(agent, "/compact now");
        Assert.NotNull(usage);
        Assert.Equal("Usage: /compact (no arguments)", Assert.IsType<CommandResult.Error>(usage.Result).Text);

        var empty = await harness.Commands.Execute(agent, "/compact");
        Assert.NotNull(empty);
        Assert.Equal("No compactable history yet.", Assert.IsType<CommandResult.Success>(empty.Result).Text);
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
            var execution = await harness.Commands.Execute(agent, "/compact");
            Assert.NotNull(execution);
            Assert.Equal(
                "Compaction is unavailable because this process has an active compaction, or the agent is not idle.",
                Assert.IsType<CommandResult.Error>(execution.Result).Text);
        }
        finally
        {
            harness.Adapter.ReleaseTurn.TrySetResult();
        }
        await agent.WhenIdle();
    }
}
