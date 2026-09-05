using System.Runtime.CompilerServices;
using Cordis;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public sealed record TokenSurfaceNode(long Seq, int Tokens, int HeuristicTokens);

public abstract record TokenMeasurementBaseline
{
    public abstract double Tokens { get; }

    public sealed record None : TokenMeasurementBaseline
    {
        public override double Tokens => 0;
    }

    public sealed record Estimated(int Estimate) : TokenMeasurementBaseline
    {
        public override double Tokens => Estimate;
    }

    public sealed record Usage(double Total, TokenUsage Reported) : TokenMeasurementBaseline
    {
        public override double Tokens => Total;
    }
}

public sealed record TokenMeasurement(
    long LogRevision,
    TokenMeasurementBaseline Baseline,
    int SurfaceDeltaTokens,
    double TotalTokens,
    int SurfaceTokens,
    IReadOnlyList<TokenSurfaceNode> Nodes);

public sealed class TokenMeter : Service, IDisposable
{
    public const string ServiceName = "tokenMeter";

    private sealed record MeasurementAnchor(
        EpochHeader? Header,
        IReadOnlyList<TokenSurfaceNode> Nodes,
        int AssistantTokens,
        TokenUsage? Usage);

    private sealed class ReplayState
    {
        public long ConsumedEvents;
        public EpochHeader? Header;
        public List<TokenSurfaceNode> Surface = [];
        public (int Turn, int Step, IReadOnlyList<TokenSurfaceNode> Nodes)? StepStart;
        public MeasurementAnchor? Anchor;
    }

    private readonly ConditionalWeakTable<Session, ReplayState> _states = new();
    private readonly Func<bool> _listener;

    public TokenMeter(Context ctx) : base(ctx, ServiceName)
    {
        _listener = ctx.On(SessionStore.EventEvent, (thisArg, args) =>
        {
            var session = (Session)args[0]!;
            if (_states.TryGetValue(session, out _))
                Sync(session);
            return new ValueTask<object?>();
        }, new EventOptions { Global = true });
    }

    public static TokenMeter Register(Context ctx) => new(ctx);

    public void Dispose() => _listener();

    public int EstimateMessage(Dsh.Llm.Message message) => TokenEstimate.EstimateMessage(message);

    public TokenMeasurement Measure(Session session)
    {
        var state = Sync(session);
        var header = state.Header;
        var nodes = (IReadOnlyList<TokenSurfaceNode>)[.. state.Surface];
        var surfaceTokens = 0;
        foreach (var node in nodes)
            surfaceTokens += node.Tokens;
        var anchor = state.Anchor;

        TokenMeasurementBaseline baseline;
        int surfaceDeltaTokens;
        if (anchor is not null && HeaderEquals(anchor.Header, header))
        {
            var anchorSurfaceTokens = anchor.Nodes.Sum(node => node.Tokens) + anchor.AssistantTokens;
            var estimatedAnchorTokens = TokenEstimate.EstimateHeader(header) + anchorSurfaceTokens;
            var usage = anchor.Usage;
            baseline = usage is not null && UsageTokens(usage) >= estimatedAnchorTokens
                ? new TokenMeasurementBaseline.Usage(UsageTokens(usage), usage)
                : new TokenMeasurementBaseline.Estimated(estimatedAnchorTokens);
            surfaceDeltaTokens = surfaceTokens - anchorSurfaceTokens;
        }
        else if (header is null && surfaceTokens == 0)
        {
            baseline = new TokenMeasurementBaseline.None();
            surfaceDeltaTokens = 0;
        }
        else
        {
            baseline = new TokenMeasurementBaseline.Estimated(TokenEstimate.EstimateHeader(header) + surfaceTokens);
            surfaceDeltaTokens = 0;
        }

        return new TokenMeasurement(
            state.ConsumedEvents,
            baseline,
            surfaceDeltaTokens,
            Math.Max(0, baseline.Tokens + surfaceDeltaTokens),
            surfaceTokens,
            nodes);
    }

    private static double UsageTokens(TokenUsage usage)
        => usage.InputTokens + (usage.CacheReadTokens ?? 0) + (usage.CacheWriteTokens ?? 0) + usage.OutputTokens;

    private static bool HeaderEquals(EpochHeader? left, EpochHeader? right)
    {
        if (left is null || right is null)
            return left is null == right is null;
        return RequestHeader.Equals(left, right);
    }

    private ReplayState Sync(Session session)
    {
        var state = _states.GetValue(session, _ => new ReplayState());
        while (state.ConsumedEvents < session.Seq)
        {
            var sessionEvent = session.EventAt(state.ConsumedEvents)!;
            FoldEvent(session, state, sessionEvent);
            state.ConsumedEvents += 1;
        }
        return state;
    }

    private void FoldEvent(Session session, ReplayState state, SessionEvent sessionEvent)
    {
        var nextHeader = state.Header;
        var nextStepStart = state.StepStart;
        var nextAnchor = state.Anchor;

        switch (sessionEvent.Type)
        {
            case SessionEventTypes.RequestHeader:
                nextHeader = RequestHeader.Canonicalize(((RequestHeaderPayload)sessionEvent.Data).Header);
                break;
            case SessionEventTypes.StepStart:
                if (state.StepStart is not null)
                    throw new InvalidOperationException(
                        $"token meter: step/start at seq {sessionEvent.Seq} arrived before turn {state.StepStart.Value.Turn}/step {state.StepStart.Value.Step} ended");
                var stepStart = (StepStartPayload)sessionEvent.Data;
                nextStepStart = (stepStart.Turn, stepStart.Step, [.. state.Surface]);
                break;
            case SessionEventTypes.StepEnd:
                var stepEnd = (StepEndPayload)sessionEvent.Data;
                if (state.StepStart is null
                    || state.StepStart.Value.Turn != stepEnd.Turn
                    || state.StepStart.Value.Step != stepEnd.Step)
                    throw new InvalidOperationException($"token meter: step/end at seq {sessionEvent.Seq} has no matching step/start event");
                nextStepStart = null;
                break;
        }

        TokenSurfaceNode? plannedNode = null;
        long replaceStart = 0, replaceEnd = 0;
        var isReplace = false;
        if (Surface.IsSurfaceEvent(sessionEvent))
        {
            var message = Surface.DeriveEventMessage(sessionEvent);
            plannedNode = new TokenSurfaceNode(
                sessionEvent.Seq,
                message is null ? 0 : TokenEstimate.EstimateMessage(message),
                message is null ? 0 : TokenEstimate.EstimateMessage(message));
            if (sessionEvent.SurfaceOp is SurfaceOp.Replace replace)
            {
                var startIdx = state.Surface.FindIndex(node => node.Seq == replace.Start);
                var endIdx = state.Surface.FindIndex(node => node.Seq == replace.End);
                if (startIdx == -1 || endIdx == -1 || startIdx > endIdx)
                    throw new InvalidOperationException(
                        $"token surface: replace at seq {sessionEvent.Seq} has invalid current range {replace.Start}-{replace.End}");
                isReplace = true;
                replaceStart = startIdx;
                replaceEnd = endIdx;
            }
        }

        if (sessionEvent.Data is AssistantMessagePayload assistant)
        {
            var stepStart = state.StepStart;
            if (stepStart is null || stepStart.Value.Turn != assistant.Turn || stepStart.Value.Step != assistant.Step)
                throw new InvalidOperationException($"token meter: assistant/message at seq {sessionEvent.Seq} has no matching step/start event");
            var eventTokens = plannedNode!.Tokens;
            nextAnchor = assistant.Usage is not null && nextHeader is not null
                ? new MeasurementAnchor(nextHeader, stepStart.Value.Nodes, EstimateProviderAssistant(session, sessionEvent, assistant, eventTokens), assistant.Usage)
                : new MeasurementAnchor(nextHeader, stepStart.Value.Nodes, eventTokens, null);
        }

        state.Header = nextHeader;
        state.StepStart = nextStepStart;
        if (plannedNode is { } node)
        {
            if (isReplace)
            {
                state.Surface.RemoveRange((int)replaceStart, (int)(replaceEnd - replaceStart + 1));
                state.Surface.Insert((int)replaceStart, node);
            }
            else
            {
                state.Surface.Add(node);
            }
        }
        state.Anchor = nextAnchor;
    }

    private static int EstimateProviderAssistant(Session session, SessionEvent sessionEvent, AssistantMessagePayload assistant, int durableEventTokens)
    {
        var sourceSeqs = sessionEvent.SourceEventSeqs;
        if (sourceSeqs is null)
            return durableEventTokens;
        var assembler = new BlockAssembler();
        var seen = new HashSet<long>();
        foreach (var seq in sourceSeqs)
        {
            if (seq >= sessionEvent.Seq)
                throw new InvalidOperationException($"token meter: assistant/message at seq {sessionEvent.Seq} source seq {seq} is not earlier");
            if (!seen.Add(seq))
                throw new InvalidOperationException($"token meter: assistant/message at seq {sessionEvent.Seq} repeats source seq {seq}");
            var sourceEvent = session.EventAt(seq)!;
            if (sourceEvent.Data is not AssistantChunkPayload chunk)
                throw new InvalidOperationException($"token meter: assistant/message at seq {sessionEvent.Seq} source seq {seq} is not assistant/chunk");
            if (chunk.Turn != assistant.Turn || chunk.Step != assistant.Step)
                throw new InvalidOperationException($"token meter: assistant/message at seq {sessionEvent.Seq} source seq {seq} belongs to another step");
            assembler.Push(chunk.Chunk);
        }
        var providerContent = assembler.Blocks();
        return providerContent.Count == 0 ? 0 : TokenEstimate.EstimateContent(providerContent) + TokenEstimate.RoleOverhead;
    }
}
