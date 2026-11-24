using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Core;

public sealed record SurfaceFoldReplacement(long Seq, long Start, long End, IReadOnlyList<long> ShadowedSeqs);

public sealed record SurfaceFoldResult(IReadOnlyList<long> Nodes, IReadOnlyList<SurfaceFoldReplacement> Replacements,
    /** 折叠时发现但**没有**让整份会话失败的问题(例如某次 replace 的来源清单缺节点): 便于诊断, 不拦加载。 */
    IReadOnlyList<string>? Damage = null);

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
        // 回退是用户明确要"回到那之前": 连表面第一个节点一起收掉是它的正常形态。
        if (sessionEvent.Data is SessionRevertPayload)
            return;
        if (eventAt(state.Nodes[0])?.Type != SessionEventTypes.SystemMessage)
            return;
        if (sessionEvent.Type == SessionEventTypes.SystemMessage && endIdx == 0)
            return;
        throw new InvalidOperationException("surface replace: only a system/message event may replace surface node 0, exactly");
    }

    private static void AssertProvenance(FoldState state, SessionEvent sessionEvent, IReadOnlyList<long> shadowedSeqs, bool strict)
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
        if (missing.Count == 0)
            return;
        // 写路径(追加)严格拒绝: 别再把这种事件写进日志。
        if (strict)
            throw new InvalidOperationException(
                $"surface replace: sourceEventSeqs must include every shadowed surface node; missing {string.Join(", ", missing)}");
        // 读路径(折叠已有日志)只记一条损伤: 一次 replace 的来源清单不全, 不该让整份会话打不开。
        state.Damage.Add($"surface replace #{sessionEvent.Seq} 的来源清单缺 {missing.Count} 个节点"
            + $"({string.Join(", ", missing.Take(8))}{(missing.Count > 8 ? ", …" : "")}); 已按当前表面应用这次替换");
    }

    private static void AssertToolResultRewrite(
    SessionEvent sessionEvent, long start, long end, IReadOnlyList<long> shadowedSeqs, Func<long, SessionEvent?> eventAt)
    {
        if (sessionEvent.Type != SessionEventTypes.ToolResult || sessionEvent.Data is not ToolResultPayload replacement)
            return;
        // 窗口会话里被替换的节点可能整个落在尾窗之前: 它仍然是"恰好一个"节点, 只是不在本地表上。
        // 用按页取回的原文核对内容; 取不回原文时放行 —— 为了核对内容而拒绝打开整份会话代价更大。
        if (shadowedSeqs.Count == 0 && start == end)
        {
            if (eventAt(start)?.Data is ToolResultPayload outside)
                AssertOnlyContentChanged(outside, replacement);
            return;
        }
        if (shadowedSeqs.Count != 1)
            throw new InvalidOperationException("tool/result surface replacement must rewrite exactly one current node");
        if (eventAt(shadowedSeqs[0])?.Data is not ToolResultPayload original)
            throw new InvalidOperationException("tool/result surface replacement must target a current tool/result");
        AssertOnlyContentChanged(original, replacement);
    }

    private static void AssertOnlyContentChanged(ToolResultPayload original, ToolResultPayload replacement)
    {
        var originalMessage = original.Message;
        var replacementMessage = replacement.Message;
        if (originalMessage.ToolSource.CallId != replacementMessage.ToolSource.CallId
            || original.Turn != replacement.Turn
            || original.Step != replacement.Step
            || !ErrorEquals(original.Error, replacement.Error)
            || !MetaEquals(original.Meta, replacement.Meta))
            throw new InvalidOperationException("tool/result surface replacement may change only content");
    }

    private static bool ErrorEquals(ToolResultErrorInfo? a, ToolResultErrorInfo? b)
        => (a is null && b is null) || (a is not null && b is not null && a.Name == b.Name && a.Code == b.Code);

    private static bool MetaEquals(JsonElement? a, JsonElement? b)
        => (a is null && b is null) || (a is { } av && b is { } bv && JsonElement.DeepEquals(av, bv));

    private abstract record Plan
    {
        public sealed record Append(long Seq) : Plan;

        public sealed record Replace(long Seq, long Start, long End, int StartIdx, int EndIdx, IReadOnlyList<long> ShadowedSeqs) : Plan;

        /** 回退: 收掉的区间**不留节点**(与摘要不同, 回退要的结果是那一段不存在)。 */
        public sealed record Revert(long Seq, long Start, long End, int StartIdx, int EndIdx, IReadOnlyList<long> ShadowedSeqs) : Plan;
    }

    private sealed class FoldState
    {
        public List<long> Nodes = [];
        /** 折叠时降级处理过的问题(读路径): 不阻断加载, 但要留痕。 */
        public List<string> Damage = [];

        public int ReplaceGeneration;
    }

    private static Plan? PlanSurfaceEvent(
        FoldState state, SessionEvent sessionEvent, long expectedSeq, Func<long, SessionEvent?> eventAt, bool strict = true)
    {
        if (sessionEvent.Seq != expectedSeq)
            throw new InvalidOperationException($"session event seq {sessionEvent.Seq} is not contiguous; expected {expectedSeq}");
        var surfaceOp = sessionEvent.SurfaceOp;
        if (sessionEvent.Data is SessionRevertPayload revert)
            return PlanRevert(state, sessionEvent, revert, surfaceOp, strict);
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
            AssertProvenance(state, sessionEvent, [], strict);
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
            AssertProvenance(state, sessionEvent, shadowedHere, strict);
            AssertToolResultRewrite(sessionEvent, replace.Start, replace.End, shadowedHere, eventAt);
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
        AssertProvenance(state, sessionEvent, shadowed, strict);
        AssertToolResultRewrite(sessionEvent, replace.Start, replace.End, shadowed, eventAt);
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
            case Plan.Revert revert:
                state.Nodes.RemoveRange(revert.StartIdx, revert.EndIdx - revert.StartIdx + 1);
                state.ReplaceGeneration += 1;
                return new SurfaceFoldReplacement(revert.Seq, revert.Start, revert.End, revert.ShadowedSeqs);
            default:
                return null;
        }
    }

    /**
     * 回退的计划: 收掉表面上所有 `>= TargetSeq` 的节点, 且**不留替换节点**。
     *
     * 校验按"日志自描述"来: 事件里写的 `SurfaceOp.Replace(start, end)` 必须正好覆盖被收掉的那一段
     * (start = 第一个被收掉的节点, end = 当时表面最后一个节点), `SourceEventSeqs` 必须给出同一批节点
     * (见 AssertProvenance)。这样别的读者折到同一条事件时会算出同一个结果, 而不是各信各的意图。
     * 表面上没有东西可收时不留 surfaceOp: 这条回退只记录"意图"。
     */
    private static Plan? PlanRevert(FoldState state, SessionEvent sessionEvent, SessionRevertPayload revert, SurfaceOp? surfaceOp, bool strict = true)
    {
        var firstIdx = -1;
        for (var index = 0; index < state.Nodes.Count; index += 1)
        {
            if (state.Nodes[index] < revert.TargetSeq)
                continue;
            firstIdx = index;
            break;
        }
        if (firstIdx < 0)
        {
            if (surfaceOp is not null)
                throw new InvalidOperationException($"session/revert at seq {sessionEvent.Seq} carries a surfaceOp but there is nothing at or after seq {revert.TargetSeq} to drop");
            return null;
        }
        if (surfaceOp is not SurfaceOp.Replace replace)
            throw new InvalidOperationException($"session/revert at seq {sessionEvent.Seq} must carry a surfaceOp replace covering the dropped range");
        var shadowed = state.Nodes.GetRange(firstIdx, state.Nodes.Count - firstIdx);
        // 窗口会话的本地表面只是整段的一段: replace.Start 可能落在本地之前(那一段本来就不在表上, 与 replace 的既有约定一致)。
        if (replace.Start > shadowed[0] || replace.End != shadowed[^1])
            throw new InvalidOperationException(
                $"session/revert at seq {sessionEvent.Seq} must cover [{replace.Start}, {replace.End}] back from seq {revert.TargetSeq}, local surface ends at {shadowed[^1]}");
        AssertProvenance(state, sessionEvent, shadowed, strict);
        return new Plan.Revert(sessionEvent.Seq, replace.Start, replace.End, firstIdx, state.Nodes.Count - 1, shadowed);
    }

    public static SurfaceFoldResult Fold(IReadOnlyList<SessionEvent> events)
    {
        var state = new FoldState();
        var replacements = new List<SurfaceFoldReplacement>();
        for (var index = 0; index < events.Count; index++)
        {
            var replacement = ApplyPlan(state, PlanSurfaceEvent(state, events[index], index, seq => events[(int)seq], strict: false));
            if (replacement is not null)
                replacements.Add(replacement);
        }
        return new SurfaceFoldResult([.. state.Nodes], replacements, [.. state.Damage]);
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
        /** 常驻窗口起点: 0 表示整份日志都在内存里, 追加前的校验因此不必等前缀折叠。 */
        private readonly long _residentFrom;
        private readonly FoldState _state;
        private readonly List<SurfaceFoldReplacement> _replacements = [];
        private long _lastProcessedSeq;
        private (SessionEvent Event, long ExpectedSeq, Plan? Plan)? _pendingPlan;

        public Manager(Func<IReadOnlyList<SessionEvent>> logProvider, long baseSeq = 0,
            Func<long, SessionEvent?>? outsideWindow = null, long residentFrom = 0,
            SurfaceFoldSnapshot? adopted = null)
        {
            _logProvider = logProvider;
            _outsideWindow = outsideWindow;
            _residentFrom = residentFrom;
            _state = new FoldState();
            _lastProcessedSeq = baseSeq == 0 ? -1 : baseSeq - 1;
            if (adopted is not { } fold || fold.Nodes.Count == 0)
                return;
            // 派生缓存命中: 折叠结果直接用, 只需从 total 之后接着折(日志只追加)。
            _state.Nodes.AddRange(fold.Nodes);
            _state.ReplaceGeneration = fold.ReplaceGeneration;
            _lastProcessedSeq = fold.TotalEvents - 1;
        }

        /** 已经折到的事件序号(含): -1 表示还没有折过。派生缓存据此判断"要不要先折一遍"。 */
        public long FoldedThrough => Volatile.Read(ref _lastProcessedSeq);

        public void ValidateNext(SessionEvent sessionEvent)
        {
            var log = _logProvider();
            var expectedSeq = log.Count;
            // 窗口会话的前缀还没折过(状态停在常驻窗口之前)时, 立刻补折等于恢复大会话时先把整段历史读一遍。
            // 这里先不做即时校验; 折叠会在第一次真正需要表面时发生, ProcessDelta 用同一套规则逐条校验。
            // 非窗口会话的 residentFrom 是 0, 这条分支永远不成立, 追加时的校验强度不变。
            if (_lastProcessedSeq + 1 < _residentFrom)
                return;
            EnsureProcessed(log);
            // 来源清单不全**不再拒绝写入**: 那是一次"哪些事件构成了这条消息"的记账问题, 不是"日志要坏掉了"。
            // 拒绝写入的代价是把整条会话冻住(读得了、写不了) —— 实测一条 2026 年早期的日志就是这样被彻底放弃的。
            // 结构性问题(序号不连续、replace 的终点不在表面上)仍然是致命的, 那些真的没法安全折叠。
            _pendingPlan = (sessionEvent, expectedSeq,
                PlanSurfaceEvent(_state, sessionEvent, expectedSeq, Accessor(log), strict: false));
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

        /** 折叠时降级处理过的问题(不阻断加载, 只留痕)。 */
        public IReadOnlyList<string> Damage
        {
            get
            {
                EnsureProcessed();
                return _state.Damage;
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
         *
         * 取不回来(那一页读不出/已释放且没有兜底)时返回 null: 折叠这一条就是"缺一条", 不能因此让整个
         * 派生视图抛异常 —— 前缀缺页不该把尾窗里的消息一起带走。
         */
        private Func<long, SessionEvent?> Accessor(IReadOnlyList<SessionEvent> log)
            => seq =>
            {
                if (seq < 0 || seq >= log.Count)
                    return _outsideWindow?.Invoke(seq);
                try
                {
                    return log[(int)seq];
                }
                catch (SessionPageUnavailableException)
                {
                    return _outsideWindow?.Invoke(seq);
                }
            };

        /**
         * 从日志重折 [from, to): 内存被重编号之后, 折叠状态必须按新号整体重算一次。
         *
         * 与 ProcessDelta 的区别是它不看"已折到哪", 而是把给定区间当作全部历史重来一遍。
         */
        public void Refold(Func<long, SessionEvent?> eventAt, long from, long to)
        {
            _state.Nodes.Clear();
            _state.ReplaceGeneration = 0;
            _state.Damage.Clear();
            _replacements.Clear();
            _pendingPlan = null;
            _lastProcessedSeq = from - 1;
            for (var seq = from; seq < to; seq += 1)
            {
                _lastProcessedSeq = seq;
                if (eventAt(seq) is not { } sessionEvent)
                    continue;
                var replacement = ApplyPlan(_state, PlanSurfaceEvent(_state, sessionEvent, seq, eventAt, strict: false));
                if (replacement is not null)
                    _replacements.Add(replacement);
            }
        }

        private void ProcessDelta(IReadOnlyList<SessionEvent> log)
        {
            var tailSeq = TailSeq(log);
            var eventAt = Accessor(log);
            for (var seq = _lastProcessedSeq + 1; seq <= tailSeq; seq++)
            {
                _lastProcessedSeq = seq;
                // 缺页的那一条折不出来, 但序号照旧往前走: 否则每次都从它重试, 尾窗永远折不到。
                if (eventAt(seq) is not { } sessionEvent)
                    continue;
                var pending = _pendingPlan;
                var replacement = pending is { } plan && ReferenceEquals(plan.Event, sessionEvent) && plan.ExpectedSeq == seq
                    ? ApplyPlan(_state, plan.Plan)
                    : ApplyPlan(_state, PlanSurfaceEvent(_state, sessionEvent, seq, eventAt, strict: false));
                if (replacement is not null)
                    _replacements.Add(replacement);
                if (pending is not null && pending.Value.ExpectedSeq <= seq)
                    _pendingPlan = null;
            }
        }
    }
}
