using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record SurfaceFoldReplacement(long Seq, long Start, long End, IReadOnlyList<long> ShadowedSeqs);

public sealed record SurfaceFoldResult(IReadOnlyList<long> Nodes, IReadOnlyList<SurfaceFoldReplacement> Replacements);

public static class Surface
{
    public static bool IsSurfaceEligibleType(string type) => SessionEventTypes.SurfaceEligible.Contains(type);

    public static bool IsSurfaceEvent(SessionEvent sessionEvent)
        => IsSurfaceEligibleType(sessionEvent.Type) && sessionEvent.SurfaceOp is not null;

    public static bool IsReplacementSurfaceEvent(SessionEvent sessionEvent)
        => IsSurfaceEvent(sessionEvent) && sessionEvent.SurfaceOp is SurfaceOp.Replace;

    public static Message? DeriveEventMessage(SessionEvent sessionEvent) => sessionEvent.Data switch
    {
        UserMessagePayload userMessage => userMessage.Message,
        SystemMessagePayload { Message.Content.Count: > 0 } systemMessage => systemMessage.Message,
        SystemMessagePayload => null,
        AssistantMessagePayload { Message.Content.Count: > 0 } assistantMessage => assistantMessage.Message,
        AssistantMessagePayload => null,
        ToolResultPayload toolResult => toolResult.Message,
        _ => null,
    };

    private static void AssertSystemMessage(SessionEvent sessionEvent)
    {
        if (sessionEvent.Type != SessionEventTypes.SystemMessage || sessionEvent.Data is not SystemMessagePayload payload)
            return;
        if (payload.Message.Role != MessageRole.System)
            throw new InvalidOperationException("system/message payload must carry a system-role message");
        if (payload.Message.Source is not PluginMessageSource)
            throw new InvalidOperationException("system/message payload must carry a plugin-sourced message");
    }

    private static void AssertSystemHeadRewrite(
        SessionEvent sessionEvent, FoldState state, int startIdx, int endIdx, Func<long, SessionEvent?> eventAt)
    {
        if (startIdx != 0 || state.Nodes.Count == 0)
            return;
        if (eventAt(state.Nodes[0])?.Type != SessionEventTypes.SystemMessage)
            return;
        if (sessionEvent.Type == SessionEventTypes.SystemMessage && endIdx == 0)
            return;
        throw new InvalidOperationException("surface replace: only a system/message event may replace surface node 0, exactly");
    }

    private static void AssertProvenance(SessionEvent sessionEvent, IReadOnlyList<long> shadowedSeqs)
    {
        var sources = new HashSet<long>();
        if (sessionEvent.SourceEventSeqs is { } raw)
        {
            if (raw.Count == 0 && sessionEvent.Type != SessionEventTypes.AssistantMessage)
                throw new InvalidOperationException("sourceEventSeqs must not be empty except on assistant/message");
            long? nonEarlierSource = null;
            foreach (var source in raw)
            {
                if (source < 0)
                    throw new InvalidOperationException($"session event \"{sessionEvent.Type}\" sourceEventSeqs must densely contain non-negative safe integers");
                sources.Add(source);
                if (nonEarlierSource is null && source >= sessionEvent.Seq)
                    nonEarlierSource = source;
            }
            if (sources.Count != raw.Count)
                throw new InvalidOperationException("sourceEventSeqs must not contain duplicates");
            if (nonEarlierSource is not null)
                throw new InvalidOperationException($"sourceEventSeqs must reference earlier events: {nonEarlierSource} >= current seq {sessionEvent.Seq}");
        }
        var missing = shadowedSeqs.Where(seq => !sources.Contains(seq)).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"surface replace: sourceEventSeqs must include every shadowed surface node; missing {string.Join(", ", missing)}");
    }

    private static void AssertToolResultRewrite(
        SessionEvent sessionEvent, IReadOnlyList<long> shadowedSeqs, Func<long, SessionEvent?> eventAt)
    {
        if (sessionEvent.Type != SessionEventTypes.ToolResult || sessionEvent.Data is not ToolResultPayload replacement)
            return;
        if (shadowedSeqs.Count != 1)
            throw new InvalidOperationException("tool/result surface replacement must rewrite exactly one current node");
        foreach (var originalSeq in shadowedSeqs)
        {
            if (eventAt(originalSeq)?.Data is not ToolResultPayload original)
                throw new InvalidOperationException("tool/result surface replacement must target a current tool/result");
            var originalMessage = original.Message;
            var replacementMessage = replacement.Message;
            if (originalMessage.ToolSource.CallId != replacementMessage.ToolSource.CallId
                || original.Turn != replacement.Turn
                || original.Step != replacement.Step
                || !ErrorEquals(original.Error, replacement.Error)
                || !MetaEquals(original.Meta, replacement.Meta))
                throw new InvalidOperationException("tool/result surface replacement may change only content");
        }
    }

    private static bool ErrorEquals(ToolResultErrorInfo? a, ToolResultErrorInfo? b)
        => (a is null && b is null) || (a is not null && b is not null && a.Name == b.Name && a.Code == b.Code);

    private static bool MetaEquals(JsonElement? a, JsonElement? b)
        => (a is null && b is null) || (a is { } av && b is { } bv && JsonElement.DeepEquals(av, bv));

    private abstract record Plan
    {
        public sealed record Append(long Seq) : Plan;

        public sealed record Replace(long Seq, long Start, long End, int StartIdx, int EndIdx, IReadOnlyList<long> ShadowedSeqs) : Plan;
    }

    private sealed class FoldState
    {
        public List<long> Nodes = [];

        public int ReplaceGeneration;
    }

    private static Plan? PlanSurfaceEvent(
        FoldState state, SessionEvent sessionEvent, long expectedSeq, Func<long, SessionEvent?> eventAt)
    {
        if (sessionEvent.Seq != expectedSeq)
            throw new InvalidOperationException($"session event seq {sessionEvent.Seq} is not contiguous; expected {expectedSeq}");
        var surfaceOp = sessionEvent.SurfaceOp;
        if (!IsSurfaceEligibleType(sessionEvent.Type))
        {
            if (surfaceOp is not null)
                throw new InvalidOperationException($"session event \"{sessionEvent.Type}\" is not surface-eligible and cannot carry surfaceOp");
            if (sessionEvent.SourceEventSeqs is not null)
                throw new InvalidOperationException($"session event \"{sessionEvent.Type}\" is not surface-eligible and cannot carry sourceEventSeqs");
            return null;
        }
        if (surfaceOp is null)
            throw new InvalidOperationException($"session event \"{sessionEvent.Type}\" is surface-eligible and requires a surfaceOp marker");
        if (surfaceOp is SurfaceOp.Append)
        {
            AssertProvenance(sessionEvent, []);
            AssertSystemMessage(sessionEvent);
            return new Plan.Append(sessionEvent.Seq);
        }
        var replace = (SurfaceOp.Replace)surfaceOp;
        var startIdx = state.Nodes.IndexOf(replace.Start);
        var endIdx = state.Nodes.IndexOf(replace.End);
        // 窗口化会话的 surface 天然只是局部一段: 跨边界 replace 的起点可能在窗口之前, 那样它本来就不在表上。
        // 此时"本地要收掉的"就是表头到终点那一段(终点也不在表上就是空), 摘要节点插到表头。
        // 事件自带的 sourceEventSeqs 是**全集**, 而 provenance 校验要求"阴影 ⊆ 来源", 所以子集成立。
        if (startIdx == -1)
        {
            var shadowedHere = endIdx < 0 ? [] : state.Nodes.GetRange(0, endIdx + 1);
            if (endIdx >= 0)
                AssertSystemHeadRewrite(sessionEvent, state, 0, endIdx, eventAt);
            AssertProvenance(sessionEvent, shadowedHere);
            AssertToolResultRewrite(sessionEvent, shadowedHere, eventAt);
            AssertSystemMessage(sessionEvent);
            return new Plan.Replace(sessionEvent.Seq, replace.Start, replace.End, 0, endIdx, shadowedHere);
        }
        if (endIdx == -1)
            throw new InvalidOperationException($"surface replace: end seq {replace.End} not found in surface");
        if (startIdx > endIdx)
            throw new InvalidOperationException($"surface replace: start seq {replace.Start} (index {startIdx}) is after end seq {replace.End} (index {endIdx})");
        AssertSystemHeadRewrite(sessionEvent, state, startIdx, endIdx, eventAt);
        AssertSystemMessage(sessionEvent);
        var shadowed = state.Nodes.GetRange(startIdx, endIdx - startIdx + 1);
        AssertProvenance(sessionEvent, shadowed);
        AssertToolResultRewrite(sessionEvent, shadowed, eventAt);
        return new Plan.Replace(sessionEvent.Seq, replace.Start, replace.End, startIdx, endIdx, shadowed);
    }

    private static SurfaceFoldReplacement? ApplyPlan(FoldState state, Plan? plan)
    {
        switch (plan)
        {
            case Plan.Append append:
                state.Nodes.Add(append.Seq);
                return null;
            case Plan.Replace replace:
                state.Nodes.RemoveRange(replace.StartIdx, replace.EndIdx - replace.StartIdx + 1);
                state.Nodes.Insert(replace.StartIdx, replace.Seq);
                state.ReplaceGeneration += 1;
                return new SurfaceFoldReplacement(replace.Seq, replace.Start, replace.End, replace.ShadowedSeqs);
            default:
                return null;
        }
    }

    public static SurfaceFoldResult Fold(IReadOnlyList<SessionEvent> events)
    {
        var state = new FoldState();
        var replacements = new List<SurfaceFoldReplacement>();
        for (var index = 0; index < events.Count; index++)
        {
            var replacement = ApplyPlan(state, PlanSurfaceEvent(state, events[index], index, seq => events[(int)seq]));
            if (replacement is not null)
                replacements.Add(replacement);
        }
        return new SurfaceFoldResult([.. state.Nodes], replacements);
    }

    /**
     * 折叠状态机。
     *
     * 日志按**绝对 seq** 取事件: baseSeq 是本次折叠的起点(fork 前缀之后, 或窗口会话的尾窗起点)。
     * 前缀不在内存里的窗口会话因此只折尾窗, 而不必把整份历史重新展开。
     */
    public sealed class Manager
    {
        private readonly Func<IReadOnlyList<SessionEvent>> _logProvider;
        /** 常驻窗口之外的事件按需取(窗口化会话); null 表示"全部在内存里"。 */
        private readonly Func<long, SessionEvent?>? _outsideWindow;
        private readonly FoldState _state;
        private readonly List<SurfaceFoldReplacement> _replacements = [];
        private long _lastProcessedSeq;
        private (SessionEvent Event, long ExpectedSeq, Plan? Plan)? _pendingPlan;

        public Manager(Func<IReadOnlyList<SessionEvent>> logProvider, long baseSeq = 0,
            Func<long, SessionEvent?>? outsideWindow = null)
        {
            _logProvider = logProvider;
            _outsideWindow = outsideWindow;
            _state = new FoldState();
            _lastProcessedSeq = baseSeq == 0 ? -1 : baseSeq - 1;
        }

        public void ValidateNext(SessionEvent sessionEvent)
        {
            var log = _logProvider();
            EnsureProcessed(log);
            var expectedSeq = log.Count;
            _pendingPlan = (sessionEvent, expectedSeq, PlanSurfaceEvent(_state, sessionEvent, expectedSeq, Accessor(log)));
        }

        public int ReplaceGeneration
        {
            get
            {
                EnsureProcessed();
                return _state.ReplaceGeneration;
            }
        }

        public IReadOnlyList<long> Nodes
        {
            get
            {
                EnsureProcessed();
                return _state.Nodes;
            }
        }

        /** 迄今折叠过的全部 replace, 按日志顺序; 与 Fold 的 Replacements 一致。 */
        public IReadOnlyList<SurfaceFoldReplacement> Replacements
        {
            get
            {
                EnsureProcessed();
                return _replacements;
            }
        }

        /** 日志末条事件的绝对 seq; 空日志返回 baseSeq - 1。 */
        private long TailSeq(IReadOnlyList<SessionEvent> log) => log.Count - 1;

        private void EnsureProcessed()
        {
            var log = _logProvider();
            EnsureProcessed(log);
        }

        private void EnsureProcessed(IReadOnlyList<SessionEvent> log)
        {
            if (_lastProcessedSeq < TailSeq(log))
                ProcessDelta(log);
        }

        /**
         * 按**绝对序号**取事件。窗口化会话里常驻窗口只覆盖尾部, 之前的事件走 `outsideWindow` 按页取 ——
         * 这样一次跨边界 replace 不再要求"整段历史都塞进窗口", 折叠到那个节点时按需取一条即可。
         */
        private Func<long, SessionEvent?> Accessor(IReadOnlyList<SessionEvent> log)
            => seq => seq >= 0 && seq < log.Count ? log[(int)seq] : _outsideWindow?.Invoke(seq);

        private void ProcessDelta(IReadOnlyList<SessionEvent> log)
        {
            var tailSeq = TailSeq(log);
            var eventAt = Accessor(log);
            for (var seq = _lastProcessedSeq + 1; seq <= tailSeq; seq++)
            {
                var sessionEvent = log[(int)seq];
                var pending = _pendingPlan;
                var replacement = pending is { } plan && ReferenceEquals(plan.Event, sessionEvent) && plan.ExpectedSeq == seq
                    ? ApplyPlan(_state, plan.Plan)
                    : ApplyPlan(_state, PlanSurfaceEvent(_state, sessionEvent, seq, eventAt));
                if (replacement is not null)
                    _replacements.Add(replacement);
                if (pending is not null && pending.Value.ExpectedSeq <= seq)
                    _pendingPlan = null;
                _lastProcessedSeq = seq;
            }
        }
    }
}
