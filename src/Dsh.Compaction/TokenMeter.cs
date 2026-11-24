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

/** 计量器的回放表面与它自己的节点表对不上(会话那边重建过表面): 由 Measure 捕获并重建, 不外泄。 */
public sealed class MeterSurfaceDesyncException(string message) : Exception(message);

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
        /** 从会话表面播种(没有重放整条日志): 省掉"助手消息必须有配对 step/start"的校验。 */
        public bool SeededFromSurface;
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
        var state = SyncChecked(session);
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

    /** 真实用量: 输入 tokens 已含缓存命中(见 TokenUsage), 再加缓存会把同一批 token 算两遍、把占用虚高近一倍。 */
    private static double UsageTokens(TokenUsage usage)
        => usage.InputTokens + usage.OutputTokens;

    private static bool HeaderEquals(EpochHeader? left, EpochHeader? right)
    {
        if (left is null || right is null)
            return left is null == right is null;
        return RequestHeader.Equals(left, right);
    }

    /**
     * 同步计量器的回放状态, 并在它与会话表面不一致时**以会话为准重建**。
     *
     * 计量器的表面是自己一面镜子(播种 + 逐条折); 一旦会话那边重建过表面(截断、窗口重开)或回放状态过期,
     * 两边就会错位, 而错位的后果是整次压缩直接失败("token-meter surface does not match")。
     * 表面以会话为准 —— 计量器只是缓存, 缓存过期就该重建, 不该让用户按不了压缩。
     */
    private ReplayState SyncChecked(Session session)
    {
        try
        {
            var state = Sync(session);
            if (SurfaceMatches(state, session))
                return state;
        }
        catch (MeterSurfaceDesyncException)
        {
            // 回放过程中就撞上了"replace 指向的节点不在自己的表上": 同样重建。
        }
        _states.Remove(session);
        return Sync(session);
    }

    private static bool SurfaceMatches(ReplayState state, Session session)
    {
        var nodes = session.SurfaceManager.Nodes;
        if (state.Surface.Count != nodes.Count)
            return false;
        for (var index = 0; index < nodes.Count; index += 1)
        {
            if (state.Surface[index].Seq != nodes[index])
                return false;
        }
        return true;
    }

    private ReplayState Sync(Session session)
    {
        var state = _states.GetValue(session, _ => new ReplayState());
        if (state.ConsumedEvents == 0 && session.Seq > 0)
            SeedFromSurface(session, state);
        while (state.ConsumedEvents < session.Seq)
        {
            var sessionEvent = session.EventAt(state.ConsumedEvents)
                ?? throw new InvalidOperationException(
                    $"token meter: event at seq {state.ConsumedEvents} could not be read back from the session log");
            FoldEvent(session, state, sessionEvent);
            state.ConsumedEvents += 1;
        }
        return state;
    }

    /**
     * 起点: 直接从**会话表面**播种, 只解压表面上的那些事件, 而不是把整条日志重放一遍。
     *
     * 表面就是这次请求要发的内容(见 Measure), 它上面的节点数比事件数小两三个数量级
     * (实测百万条事件的会话只有 88 个节点), 重放整条日志等于为了几万个序号去解析上百万条事件。
     * 代价是"上一个 step 是否开着"无从得知(那要 step/start, 而它不在表面上), 所以置 SeededFromSurface:
     * 之后的助手消息只更新表面与令牌, 不再要求配对的 step/start。
     */
    private static void SeedFromSurface(Session session, ReplayState state)
    {
        var header = session.RequestHeader();
        state.Header = header is null ? null : RequestHeader.Canonicalize(header);
        foreach (var seq in session.SurfaceManager.Nodes)
        {
            var sessionEvent = session.EventAt(seq)
                ?? throw new InvalidOperationException(
                    $"token meter: surface seq {seq} could not be read back from the session log");
            var message = Surface.DeriveEventMessage(sessionEvent);
            var tokens = message is null ? 0 : TokenEstimate.EstimateMessage(message);
            state.Surface.Add(new TokenSurfaceNode(seq, tokens, tokens));
        }
        state.ConsumedEvents = session.Seq;
        state.SeededFromSurface = true;
        RecoverAnchor(session, state);
    }

    /**
     * 播种之后补回锚点: 锚点是"上一次请求实际发出去的内容 + 它报回来的用量",
     * 少了它, 压力会从"整份表面的估算值"重新起算, 而不是 provider 报的真实占用。
     * 找的是日志末尾最近的、带 usage 的助手消息(它一定是表面节点), 只看末尾几百条事件。
     */
    private static void RecoverAnchor(Session session, ReplayState state)
    {
        for (var seq = session.Seq - 1; seq >= 0 && seq > session.Seq - AnchorScanEvents; seq -= 1)
        {
            if (session.EventAt(seq) is not { Data: AssistantMessagePayload { Usage: not null } assistant } sessionEvent)
                continue;
            if (sessionEvent.SurfaceOp is null)
                continue;
            // 锚点之后一旦发生过 replace(压缩/剪枝), 表面就不是"那次请求时的那份"了:
            // 用量基线配新表面会把差额算错(实测压缩后仍显示压缩前的占用)。这种情况宁可不给锚点。
            if (SurfaceChangedAfter(session, seq))
                return;
            var index = state.Surface.FindIndex(node => node.Seq == seq);
            if (index < 0)
                return;
            var eventTokens = state.Surface[index].Tokens;
            var header = state.Header ?? session.RequestHeader();
            state.Anchor = header is null
                ? null
                : new MeasurementAnchor(header,
                    state.Surface.Take(index).ToList(),
                    EstimateProviderAssistant(session, sessionEvent, assistant, eventTokens),
                    assistant.Usage);
            return;
        }
    }

    /** 锚点之后有没有改过表面(replace): 只看锚点之后的那一小段事件。 */
    private static bool SurfaceChangedAfter(Session session, long anchorSeq)
    {
        for (var seq = anchorSeq + 1; seq < session.Seq; seq += 1)
        {
            if (session.EventAt(seq)?.SurfaceOp is SurfaceOp.Replace)
                return true;
        }
        return false;
    }

    /** 补锚点时往回找的事件数上限: 一次请求的收尾事件就在末尾, 几百条足够, 免得把尾巴读成整段历史。 */
    private const int AnchorScanEvents = 2048;

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
                    throw new MeterSurfaceDesyncException(
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
            {
                // 播种自表面时拿不到 step/start(它不在表面上): 只更新表面与令牌, 不记锚点。
                if (!state.SeededFromSurface)
                    throw new InvalidOperationException($"token meter: assistant/message at seq {sessionEvent.Seq} has no matching step/start event");
                state.Header = nextHeader;
                state.StepStart = nextStepStart;
                if (plannedNode is { } seeded)
                {
                    if (isReplace)
                    {
                        state.Surface.RemoveRange((int)replaceStart, (int)(replaceEnd - replaceStart + 1));
                        state.Surface.Insert((int)replaceStart, seeded);
                    }
                    else
                    {
                        state.Surface.Add(seeded);
                    }
                }
                return;
            }
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
