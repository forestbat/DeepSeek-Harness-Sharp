using System.Runtime.CompilerServices;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

public static class ToolPairing
{
    private sealed class BalanceCache
    {
        public int Generation;
        public List<bool> CutBalanced = [true];
        public Dictionary<long, int> IndexBySeq = [];
        public int InProgressToolCalls;
    }

    private static readonly ConditionalWeakTable<Session, BalanceCache> Caches = new();

    private static int EventDelta(SessionEvent sessionEvent) => sessionEvent.Data switch
    {
        AssistantMessagePayload assistant => assistant.Message.Content.Count(block => block is ToolCallBlock),
        ToolResultPayload => -1,
        _ => 0,
    };

    private static BalanceCache ExtendCache(Session session, BalanceCache cache, IReadOnlyList<long> seqs)
    {
        var processed = cache.CutBalanced.Count - 1;
        var pendingCuts = new List<bool>();
        var inProgress = cache.InProgressToolCalls;
        for (var index = processed; index < seqs.Count; index++)
        {
            var seq = seqs[index];
            var sessionEvent = session.EventAt(seq);
            if (sessionEvent is null || sessionEvent.Seq != seq)
                throw new InvalidOperationException($"tool-pairing balance: surface seq {seq} has no matching session event (corrupt surface)");
            inProgress += EventDelta(sessionEvent);
            if (inProgress < 0)
                throw new InvalidOperationException($"tool-pairing balance: tool/result at surface seq {seq} has no matching tool-call (corrupt surface)");
            pendingCuts.Add(inProgress == 0);
            cache.IndexBySeq[seq] = index;
        }
        cache.CutBalanced.AddRange(pendingCuts);
        cache.InProgressToolCalls = inProgress;
        return cache;
    }

    private static BalanceCache CacheFor(Session session)
    {
        var seqs = session.SurfaceManager.Nodes;
        var generation = session.SurfaceManager.ReplaceGeneration;
        var cache = Caches.GetValue(session, _ => new BalanceCache());
        if (cache.Generation != generation || cache.CutBalanced.Count - 1 > seqs.Count)
        {
            cache.Generation = generation;
            cache.CutBalanced = [true];
            cache.IndexBySeq = new Dictionary<long, int>();
            cache.InProgressToolCalls = 0;
            return ExtendCache(session, cache, seqs);
        }
        if (cache.CutBalanced.Count - 1 < seqs.Count)
            return ExtendCache(session, cache, seqs);
        return cache;
    }

    private static bool CutBalance(BalanceCache cache, long seq, int offset)
    {
        if (!cache.IndexBySeq.TryGetValue(seq, out var index))
            throw new InvalidOperationException($"tool-pairing balance: surface seq {seq} not found");
        return cache.CutBalanced[index + offset];
    }

    public static bool BalancedBefore(Session session, long seq) => CutBalance(CacheFor(session), seq, 0);

    public static bool BalancedAfter(Session session, long seq) => CutBalance(CacheFor(session), seq, 1);
}
