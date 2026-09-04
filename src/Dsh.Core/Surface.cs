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

    public static bool IsAppendSurfaceEvent(SessionEvent sessionEvent)
        => IsSurfaceEvent(sessionEvent) && sessionEvent.SurfaceOp is SurfaceOp.Append;

    public static bool IsReplacementSurfaceEvent(SessionEvent sessionEvent)
        => IsSurfaceEvent(sessionEvent) && sessionEvent.SurfaceOp is SurfaceOp.Replace;

    public static Message? DeriveEventMessage(SessionEvent sessionEvent) => sessionEvent.Data switch
    {
        UserMessagePayload userMessage => userMessage.Message,
        AssistantMessagePayload { Message.Content.Count: > 0 } assistantMessage => assistantMessage.Message,
        AssistantMessagePayload => null,
        ToolResultPayload toolResult => toolResult.Message,
        _ => null,
    };

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

    private static void AssertToolResultRewrite(SessionEvent sessionEvent, IReadOnlyList<long> shadowedSeqs, IReadOnlyList<SessionEvent> events, long baseSeq)
    {
        if (sessionEvent.Type != SessionEventTypes.ToolResult || sessionEvent.Data is not ToolResultPayload replacement)
            return;
        if (shadowedSeqs.Count != 1)
            throw new InvalidOperationException("tool/result surface replacement must rewrite exactly one current node");
        foreach (var originalSeq in shadowedSeqs)
        {
            if (events[(int)(originalSeq - baseSeq)].Data is not ToolResultPayload original)
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

    private static Plan? PlanSurfaceEvent(FoldState state, SessionEvent sessionEvent, long expectedSeq, IReadOnlyList<SessionEvent> events, long baseSeq)
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
            return new Plan.Append(sessionEvent.Seq);
        }
        var replace = (SurfaceOp.Replace)surfaceOp;
        var startIdx = state.Nodes.IndexOf(replace.Start);
        if (startIdx == -1)
            throw new InvalidOperationException($"surface replace: start seq {replace.Start} not found in surface");
        var endIdx = state.Nodes.IndexOf(replace.End);
        if (endIdx == -1)
            throw new InvalidOperationException($"surface replace: end seq {replace.End} not found in surface");
        if (startIdx > endIdx)
            throw new InvalidOperationException($"surface replace: start seq {replace.Start} (index {startIdx}) is after end seq {replace.End} (index {endIdx})");
        var shadowed = state.Nodes.GetRange(startIdx, endIdx - startIdx + 1);
        AssertProvenance(sessionEvent, shadowed);
        AssertToolResultRewrite(sessionEvent, shadowed, events, baseSeq);
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
            var replacement = ApplyPlan(state, PlanSurfaceEvent(state, events[index], index, events, 0));
            if (replacement is not null)
                replacements.Add(replacement);
        }
        return new SurfaceFoldResult([..state.Nodes], replacements);
    }

    public sealed class Manager
    {
        private readonly IReadOnlyList<SessionEvent> _log;
        private readonly long _baseSeq;
        private readonly FoldState _state = new();
        private long _lastProcessedSeq;
        private (SessionEvent Event, long ExpectedSeq, Plan? Plan)? _pendingPlan;

        public Manager(IReadOnlyList<SessionEvent> log, long baseSeq = 0)
        {
            _log = log;
            _baseSeq = baseSeq;
            _lastProcessedSeq = baseSeq == 0 ? -1 : baseSeq - 1;
        }

        public void ValidateNext(SessionEvent sessionEvent)
        {
            if (_lastProcessedSeq < _baseSeq + _log.Count - 1)
                ProcessDelta();
            var expectedSeq = _baseSeq + _log.Count;
            _pendingPlan = (sessionEvent, expectedSeq, PlanSurfaceEvent(_state, sessionEvent, expectedSeq, _log, _baseSeq));
        }

        public int ReplaceGeneration
        {
            get
            {
                if (_lastProcessedSeq < _baseSeq + _log.Count - 1)
                    ProcessDelta();
                return _state.ReplaceGeneration;
            }
        }

        public IReadOnlyList<long> Nodes
        {
            get
            {
                if (_lastProcessedSeq < _baseSeq + _log.Count - 1)
                    ProcessDelta();
                return _state.Nodes;
            }
        }

        private void ProcessDelta()
        {
            var tailSeq = _baseSeq + _log.Count - 1;
            for (var seq = _lastProcessedSeq + 1; seq <= tailSeq; seq++)
            {
                var index = (int)(seq - _baseSeq);
                var sessionEvent = _log[index];
                var pending = _pendingPlan;
                if (pending is { } plan && ReferenceEquals(plan.Event, sessionEvent) && plan.ExpectedSeq == seq)
                {
                    ApplyPlan(_state, plan.Plan);
                }
                else
                {
                    ApplyPlan(_state, PlanSurfaceEvent(_state, sessionEvent, seq, _log, _baseSeq));
                }
                if (pending is not null && pending.Value.ExpectedSeq <= seq)
                    _pendingPlan = null;
                _lastProcessedSeq = seq;
            }
        }
    }
}
