using System.Runtime.CompilerServices;
using Dsh.Runtime;
using Dsh.Runtime.Events;
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
        public readonly List<TokenSurfaceNode> Surface = [];
        public (int Turn, int Step, IReadOnlyList<TokenSurfaceNode> Nodes)? StepStart;
        public MeasurementAnchor? Anchor;
    }

    private readonly ConditionalWeakTable<Session, ReplayState> _states = new();
    private readonly Func<bool>[] _listeners;

    public TokenMeter(Context ctx) : base(ctx, ServiceName)
    {
        _listeners =
        [
            ctx.On<SessionEventNotification>(notification =>
            {
                if (_states.TryGetValue(notification.Session, out _))
                    Sync(notification.Session);
            }, new EventOptions { Global = true }),
            // 截断后 seq 会复用, ConsumedEvents 越过新日志末尾会让后续事件被跳过; 丢弃回放状态, 下次测量从截断后的日志重放。
            ctx.On<SessionTruncateNotification>(
                notification => _states.Remove(notification.Session),
                new EventOptions { Global = true }),
        ];
    }

    public static TokenMeter Register(Context ctx) => new(ctx);

    public void Dispose()
    {
        foreach (var unsubscribe in _listeners)
            unsubscribe();
    }

    public int EstimateMessage(Llm.Message message) => TokenEstimate.EstimateMessage(message);

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

    /** 真实用量: InputTokens 已含缓存命中(见 TokenUsage), 再加缓存会把同一批 token 算两遍、把占用虚高近一倍。 */
    private static double UsageTokens(TokenUsage usage)
        => usage.InputTokens + usage.OutputTokens;

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
                var startIdx = state.Surface.FindIndex(candidate => candidate.Seq == replace.Start);
                var endIdx = state.Surface.FindIndex(candidate => candidate.Seq == replace.End);
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
            /*
             * 锚点记录"这次请求实际发出去的表面": 用当前表面而不是 step/start 时的快照。
             * 系统消息与本步的用户消息都在 step/start 之后才写入表面, 却已经在这次请求里发出去了;
             * 若按开始时的快照算, 它们会被当成"请求之后新增的内容"再计一次(实测多算 193 token)。
             */
            IReadOnlyList<TokenSurfaceNode> anchorNodes = [.. state.Surface];
            nextAnchor = assistant.Usage is not null && nextHeader is not null
                ? new MeasurementAnchor(nextHeader, anchorNodes, EstimateProviderAssistant(session, sessionEvent, assistant, eventTokens), assistant.Usage)
                : new MeasurementAnchor(nextHeader, anchorNodes, eventTokens, null);
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
