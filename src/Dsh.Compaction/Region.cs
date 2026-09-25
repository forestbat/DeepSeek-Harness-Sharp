using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public enum CompactionOwner
{
    CurrentTurn,
    Standalone,
}

public enum CompactionStability
{
    WholeSurface,
    SelectedSpan,
}

public sealed record CompactionTransactionOptions(
    CompactionOwner Owner,
    CompactionStability Stability,
    string? SourceCommandId = null,
    Func<Task>? Flush = null,
    string? Focus = null);

public sealed record SurfaceSelection(
    long Start,
    long End,
    int StartIdx,
    int EndIdx,
    IReadOnlyList<long> ShadowedSeqs);

public sealed record PreparedCompaction(
    SurfaceSelection Selection,
    TokenMeasurement Measurement,
    IReadOnlyList<TokenSurfaceNode> SelectedNodes,
    int ShadowedTokenCount,
    int ShadowedRouteTokenCount,
    SummarizationInput Input);

public sealed record SummarizedCompaction(
    PreparedCompaction Prepared,
    SummaryResult SummaryResult,
    UserMessage CheckpointMessage);

public sealed class SurfaceChangedError : Exception
{
    public SurfaceChangedError(string message, Exception? innerException = null) : base(message, innerException) { }
}

public delegate Task<SummaryResult> RegionSummarize(SummarizationInput input, IAgent agent, CancellationToken signal);

public static class CompactionRegion
{
    private readonly record struct TurnSpan(int StartIdx, int EndIdx);

    /** 选区尾部保留:先按最近 tailTurns 个 user turn 定位边界,再用 token 预算夹紧,最后对齐到平衡切点。 */
    public static ShadowedRange? SelectCompactableRange(
        Session session,
        TokenMeasurement measurement,
        int retainTokens,
        int tailTurns)
    {
        var pricedNodes = measurement.Nodes;
        if (pricedNodes.Count == 0)
            return null;
        var surfaceNodes = session.SurfaceManager.Nodes;
        if (surfaceNodes.Count != pricedNodes.Count
            || surfaceNodes.Where((seq, index) => seq != pricedNodes[index].Seq).Any())
            throw new InvalidOperationException("compaction: token-meter surface does not match the current session surface");

        var firstEligibleIdx = 0;
        while (firstEligibleIdx < surfaceNodes.Count
            && session.EventAt(surfaceNodes[firstEligibleIdx])?.Type == SessionEventTypes.SystemMessage)
        {
            firstEligibleIdx++;
        }

        var lastStepStart = LastBalancedCut(session, surfaceNodes, firstEligibleIdx);
        var keepFromIdx = SelectRetentionStart(session, pricedNodes, retainTokens, tailTurns, lastStepStart);
        if (keepFromIdx <= firstEligibleIdx)
            return null;
        while (keepFromIdx > firstEligibleIdx)
        {
            if (ToolPairing.BalancedBefore(session, surfaceNodes[keepFromIdx]))
                break;
            keepFromIdx -= 1;
        }
        if (keepFromIdx <= firstEligibleIdx)
            return null;
        return new ShadowedRange(surfaceNodes[firstEligibleIdx], surfaceNodes[keepFromIdx - 1]);
    }

    private static int SelectRetentionStart(
        Session session,
        IReadOnlyList<TokenSurfaceNode> nodes,
        int retainTokens,
        int tailTurns,
        int lastStepStart)
    {
        var prefix = PrefixTokens(nodes);
        var turns = UserTurns(session, nodes, tailTurns);
        var total = 0;
        var keep = -1;
        for (var index = turns.Count - 1; index >= 0; index--)
        {
            var turn = turns[index];
            var size = prefix[turn.EndIdx] - prefix[turn.StartIdx];
            if (total + size <= retainTokens)
            {
                total += size;
                keep = turn.StartIdx;
                continue;
            }
            var split = SplitTurn(prefix, turn, retainTokens - total);
            if (split >= 0)
                keep = split;
            break;
        }
        if (keep < 0)
            return lastStepStart;
        return Math.Min(keep, lastStepStart);
    }

    /** 按 user 消息切分 surface;插件 checkpoint 等非 user 消息不构成 turn,随头部一起被重摘要。 */
    private static List<TurnSpan> UserTurns(Session session, IReadOnlyList<TokenSurfaceNode> nodes, int tailTurns)
    {
        var starts = new List<int>();
        for (var index = 0; index < nodes.Count; index++)
        {
            if (session.EventAt(nodes[index].Seq)?.Data is UserMessagePayload { Message.Source: UserMessageSource })
                starts.Add(index);
        }
        var turns = new List<TurnSpan>();
        var first = Math.Max(0, starts.Count - Math.Max(tailTurns, 0));
        for (var index = first; index < starts.Count; index++)
        {
            var end = index + 1 < starts.Count ? starts[index + 1] : nodes.Count;
            turns.Add(new TurnSpan(starts[index], end));
        }
        return turns;
    }

    private static int SplitTurn(IReadOnlyList<int> prefix, TurnSpan turn, int budget)
    {
        if (budget <= 0 || turn.EndIdx - turn.StartIdx <= 1)
            return -1;
        for (var start = turn.StartIdx + 1; start < turn.EndIdx; start++)
        {
            if (prefix[turn.EndIdx] - prefix[start] <= budget)
                return start;
        }
        return -1;
    }

    private static IReadOnlyList<int> PrefixTokens(IReadOnlyList<TokenSurfaceNode> nodes)
    {
        var prefix = new int[nodes.Count + 1];
        for (var index = 0; index < nodes.Count; index++)
            prefix[index + 1] = prefix[index] + nodes[index].Tokens;
        return prefix;
    }

    private static int LastBalancedCut(Session session, IReadOnlyList<long> surfaceNodes, int firstEligibleIdx)
    {
        for (var index = surfaceNodes.Count - 1; index > firstEligibleIdx; index--)
        {
            if (ToolPairing.BalancedBefore(session, surfaceNodes[index]))
                return index;
        }
        return firstEligibleIdx;
    }

    public static async Task<CompactionResult> CompactSurfaceRegion(
        TokenMeter meter,
        RegionSummarize summarize,
        Session session,
        long start,
        long end,
        IAgent agent,
        CompactionTransactionOptions options,
        CancellationToken signal = default)
    {
        var standalone = options.Owner == CompactionOwner.Standalone;
        if (standalone)
            signal.ThrowIfCancellationRequested();
        var selection = ValidateSurfaceRegion(session, start, end);
        var entryState = InspectCompactionEntryState(session);
        AssertCompactionInactive(entryState.UnmatchedCompactionStart, entryState.LatestEndSeedSeq, "compaction");

        int? owner;
        if (standalone)
        {
            if (entryState.OpenTurn is not null)
                throw new ManualCompactionError(ManualCompactionErrorCode.Busy, "manual compaction: the session already has an open turn");
            owner = null;
        }
        else
        {
            owner = entryState.OpenTurn
                ?? throw new InvalidOperationException("compactRegion: no open turn — automatic compaction events must be enclosed in a turn");
        }

        var compactionId = CompactionId.Create(Guid.NewGuid().ToString());
        var startEvent = session.Append(new CompactionStartPayload(compactionId, options.SourceCommandId, owner));
        Exception? failure = null;
        var failureStage = ManualCompactionErrorCode.Summary;
        Exception? flushFailure = null;
        CompactionResult? result = null;
        var closed = false;
        var closing = false;
        var stageCommitted = false;

        try
        {
            var prepared = PrepareCompaction(meter, session, selection, options.Focus);
            var summarized = await SummarizeCompaction(meter, summarize, prepared, agent, compactionId, options.SourceCommandId, signal);
            if (standalone)
                signal.ThrowIfCancellationRequested();
            AssertStable(meter, session, options.Stability, prepared);
            stageCommitted = true;
            var pending = CommitCompactionBody(session, startEvent, summarized);
            closing = true;
            var endEvent = session.Append(new CompactionEndPayload(compactionId, options.SourceCommandId, owner));
            closed = true;
            result = pending with { EndSeq = endEvent.Seq };
        }
        catch (Exception error)
        {
            failure = error;
            failureStage = closing ? ManualCompactionErrorCode.Commit : stageCommitted ? ManualCompactionErrorCode.Commit : ManualCompactionErrorCode.Summary;
            if (!closing)
            {
                try
                {
                    session.Append(new CompactionEndPayload(compactionId, options.SourceCommandId, owner, LlmFailureClassifiers.ErrorChain(error)));
                    closed = true;
                }
                catch (Exception closeError)
                {
                    failure = closeError;
                    failureStage = ManualCompactionErrorCode.Commit;
                }
            }
        }

        if (closed && options.Flush is not null)
        {
            try
            {
                await options.Flush();
            }
            catch (Exception error)
            {
                flushFailure = error;
            }
        }

        if (standalone)
            signal.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            if (standalone)
                ThrowManualFailure(failure, failureStage);
            throw failure;
        }
        if (flushFailure is not null)
            throw new ManualCompactionError(ManualCompactionErrorCode.Persistence, "manual compaction durability checkpoint failed", flushFailure);
        return result ?? throw new InvalidOperationException("compaction committed without a result");
    }

    private static void ThrowManualFailure(Exception error, ManualCompactionErrorCode stage)
    {
        switch (stage)
        {
            case ManualCompactionErrorCode.Commit:
                throw new ManualCompactionError(ManualCompactionErrorCode.Commit, "manual compaction did not commit cleanly", error);
            default:
                if (error is SurfaceChangedError)
                    throw new ManualCompactionError(ManualCompactionErrorCode.Changed, "the compacted history changed during manual compaction", error);
                throw new ManualCompactionError(ManualCompactionErrorCode.Summary, "manual compaction could not produce a smaller summary", error);
        }
    }

    public static void AssertNoActiveCompaction(Session session, string stage)
    {
        var entryState = InspectCompactionEntryState(session);
        AssertCompactionInactive(entryState.UnmatchedCompactionStart, entryState.LatestEndSeedSeq, stage);
    }

    private static void AssertCompactionInactive(SessionEvent? unmatchedCompactionStart, long? latestEndSeedSeq, string stage)
    {
        if (unmatchedCompactionStart is null
            || (latestEndSeedSeq is { } seedSeq && seedSeq > unmatchedCompactionStart.Seq))
            return;
        throw new ManualCompactionError(
            ManualCompactionErrorCode.Busy,
            $"{stage}: compaction already in progress; the session compaction lock is already active");
    }

    private static int IndexOfSeq(IReadOnlyList<long> nodes, long seq)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            if (nodes[index] == seq)
                return index;
        }
        return -1;
    }

    public static SurfaceSelection ValidateSurfaceRegion(Session session, long start, long end)
    {
        var nodes = session.SurfaceManager.Nodes;
        var startIdx = IndexOfSeq(nodes, start);
        var endIdx = IndexOfSeq(nodes, end);
        if (startIdx == -1)
            throw new InvalidOperationException($"compactRegion: start seq {start} not found in surface");
        if (endIdx == -1)
            throw new InvalidOperationException($"compactRegion: end seq {end} not found in surface");
        if (startIdx > endIdx)
            throw new InvalidOperationException(
                $"compactRegion: start seq {start} (position {startIdx}) is after end seq {end} (position {endIdx}) on the surface");
        if (!ToolPairing.BalancedBefore(session, nodes[startIdx]))
            throw new InvalidOperationException($"compactRegion: start seq {start} is not a balanced boundary (would split a step's tool-call/result pair)");
        if (!ToolPairing.BalancedAfter(session, nodes[endIdx]))
            throw new InvalidOperationException($"compactRegion: end seq {end} is not a balanced boundary (would split a step, or the step is still open)");
        return new SurfaceSelection(start, end, startIdx, endIdx, [.. nodes.Skip(startIdx).Take(endIdx - startIdx + 1)]);
    }

    private static PreparedCompaction PrepareCompaction(TokenMeter meter, Session session, SurfaceSelection selection, string? focus)
    {
        var measurement = meter.Measure(session);
        var selectedNodes = (IReadOnlyList<TokenSurfaceNode>)[.. measurement.Nodes.Skip(selection.StartIdx).Take(selection.EndIdx - selection.StartIdx + 1)];
        if (selectedNodes.Count != selection.ShadowedSeqs.Count
            || selectedNodes.Where((node, index) => node.Seq != selection.ShadowedSeqs[index]).Any())
            throw new SurfaceChangedError("compaction: selected surface changed before summarization began");
        return new PreparedCompaction(
            selection,
            measurement,
            selectedNodes,
            selectedNodes.Sum(node => node.HeuristicTokens),
            selectedNodes.Sum(node => node.Tokens),
            BuildSummarizationInput(session, selection.ShadowedSeqs, focus));
    }

    private static async Task<SummarizedCompaction> SummarizeCompaction(
        TokenMeter meter,
        RegionSummarize summarize,
        PreparedCompaction prepared,
        IAgent agent,
        CompactionId compactionId,
        string? sourceCommandId,
        CancellationToken signal)
    {
        var summaryResult = await summarize(prepared.Input, agent, signal);
        var checkpointMessage = MessageFactory.CreateUserMessage(
            Summarizer.FrameSummary(summaryResult.Summary),
            CompactionCheckpoint.Source(compactionId, sourceCommandId));
        var framedSummaryTokenCount = meter.EstimateMessage(checkpointMessage);
        if (framedSummaryTokenCount >= prepared.ShadowedRouteTokenCount)
            throw new InvalidOperationException(
                $"summary is not smaller than the shadowed content ({framedSummaryTokenCount} estimated framed tokens >= {prepared.ShadowedRouteTokenCount})");
        return new SummarizedCompaction(prepared, summaryResult, checkpointMessage);
    }

    private static void AssertStable(TokenMeter meter, Session session, CompactionStability stability, PreparedCompaction prepared)
    {
        if (stability == CompactionStability.WholeSurface)
        {
            var current = meter.Measure(session);
            if (!current.Nodes.SequenceEqual(prepared.Measurement.Nodes))
                throw new SurfaceChangedError("compaction: session surface changed during summarization");
            return;
        }
        SurfaceSelection currentSelection;
        try
        {
            currentSelection = ValidateSurfaceRegion(session, prepared.Selection.Start, prepared.Selection.End);
        }
        catch (Exception error)
        {
            throw new SurfaceChangedError("compaction: the selected span is no longer a valid replacement target", error);
        }
        if (!currentSelection.ShadowedSeqs.SequenceEqual(prepared.Selection.ShadowedSeqs))
            throw new SurfaceChangedError("compaction: the selected span changed during summarization");
        var measured = meter.Measure(session).Nodes.Skip(currentSelection.StartIdx).Take(currentSelection.EndIdx - currentSelection.StartIdx + 1);
        if (!measured.SequenceEqual(prepared.SelectedNodes))
            throw new SurfaceChangedError("compaction: the selected span was rewritten during summarization");
    }

    private static CompactionResult CommitCompactionBody(Session session, SessionEvent startEvent, SummarizedCompaction summarized)
    {
        var prepared = summarized.Prepared;
        var summaryResult = summarized.SummaryResult;
        var selection = prepared.Selection;
        var startPayload = (CompactionStartPayload)startEvent.Data;
        var summaryEvent = session.Append(new CompactionSummaryPayload(
            startPayload.CompactionId,
            startPayload.SourceCommandId,
            summaryResult.Summary,
            new ShadowedRange(selection.Start, selection.End),
            [.. selection.ShadowedSeqs],
            prepared.ShadowedTokenCount,
            summaryResult.Provider,
            summaryResult.Model,
            summaryResult.MaxTokens,
            summaryResult.Usage,
            summaryResult.RawOutput,
            true));
        session.Append(
            new UserMessagePayload(summarized.CheckpointMessage),
            new SurfaceOp.Replace(selection.Start, selection.End),
            [startEvent.Seq, summaryEvent.Seq, .. selection.ShadowedSeqs]);
        return new CompactionResult(
            startPayload.CompactionId,
            startPayload.SourceCommandId,
            startEvent.Seq,
            summaryEvent.Seq,
            0,
            summaryResult.Summary,
            new ShadowedRange(selection.Start, selection.End),
            [.. selection.ShadowedSeqs],
            prepared.ShadowedTokenCount);
    }

    private static SummarizationInput BuildSummarizationInput(Session session, IReadOnlyList<long> shadowedSeqs, string? focus)
    {
        var header = session.RequestHeader();
        var systemNodes = session.SurfaceManager.Nodes;
        var system = systemNodes.Count > 0
            && session.EventAt(systemNodes[0])?.Data is SystemMessagePayload { Message.Content: [TextBlock headText, ..] }
                ? headText.Text
                : null;
        var regionMessages = shadowedSeqs
            .Select(seq => Surface.DeriveEventMessage(session.EventAt(seq)!))
            .OfType<Message>()
            .ToList();
        return new SummarizationInput(system, header?.Tools, regionMessages, focus);
    }

    private static (int? OpenTurn, SessionEvent? UnmatchedCompactionStart, long? LatestEndSeedSeq) InspectCompactionEntryState(Session session)
    {
        int? openTurn = null;
        var openTurnStateKnown = false;
        SessionEvent? unmatchedCompactionStart = null;
        var compactionEntryStateKnown = false;
        long? latestEndSeedSeq = null;
        for (var seq = session.Seq - 1; seq >= 0; seq--)
        {
            var sessionEvent = session.EventAt(seq)!;
            if (latestEndSeedSeq is null && sessionEvent.Type == SessionEventTypes.SessionEndSeed)
                latestEndSeedSeq = sessionEvent.Seq;
            if (!compactionEntryStateKnown)
            {
                if (sessionEvent.Type == CompactionEventTypes.Start)
                {
                    unmatchedCompactionStart = sessionEvent;
                    compactionEntryStateKnown = true;
                }
                else if (sessionEvent.Type == CompactionEventTypes.End)
                {
                    compactionEntryStateKnown = true;
                }
            }
            if (!openTurnStateKnown)
            {
                if (sessionEvent.Data is TurnStartPayload turnStart)
                {
                    openTurn = turnStart.Turn;
                    openTurnStateKnown = true;
                }
                else if (sessionEvent.Type == SessionEventTypes.TurnEnd)
                {
                    openTurnStateKnown = true;
                }
            }
            if (openTurnStateKnown && compactionEntryStateKnown && latestEndSeedSeq is not null)
                break;
        }
        return (openTurn, unmatchedCompactionStart, latestEndSeedSeq);
    }
}
