using Dsh.Core;
using Dsh.Interaction;

namespace Dsh.Tui;

/**
 * 转录历史的轻量索引: 用户消息、回合起点、回退区间。
 *
 * `/timestamp` 与 `/timestamp revert` 原来每次都 Session.SnapshotEvents() 整扫 —— 窗口化会话里那等于
 * 按页把整份日志读回来(百万条会话每次打开时间线都要读上百页), 而回退区间只从常驻尾窗算, 前缀里的
 * 回退块会漏掉。这里把三者收在一份按序累积的索引里: 渲染时顺手累积已载入区间, 需要更早的区间时
 * 按页补一段再合并(与滚轮补页同一个入口), 之后这两条命令都只读内存。
 *
 * 覆盖语义: [_coveredFrom, 末尾) 的用户消息与回合起点是完整的。补页把 _coveredFrom 往下推。
 */
internal sealed class TranscriptHistoryIndex
{
    private readonly List<SessionTimeline.Entry> _messages = [];
    private readonly List<long> _turnStarts = [];
    private readonly List<(long From, long To)> _reverted = [];

    /** 索引覆盖起点: 0 表示整段历史都已在索引里。 */
    public long CoveredFrom { get; private set; }

    /** 已收录的用户消息(按 seq 递增, 已被回退的已剔除)。 */
    public IReadOnlyList<SessionTimeline.Entry> Messages => _messages;

    public bool Contains(long seq) => SessionTimeline.Contains(_reverted, seq);

    /** 丢弃 fromSeq 及之后的条目(重放前调用): 更早的区间仍然有效。 */
    public void KeepBelow(long fromSeq)
    {
        _messages.RemoveAll(entry => entry.Seq >= fromSeq);
        _turnStarts.RemoveAll(seq => seq >= fromSeq);
    }

    /** 整份重建: 只有回退/重编号这种"日志视图整体变了"的情况需要。 */
    public void Reset(long coveredFrom)
    {
        _messages.Clear();
        _turnStarts.Clear();
        CoveredFrom = coveredFrom;
    }

    /** 标记 [fromSeq, 末尾) 已经完整收录。 */
    public void CoverFrom(long fromSeq) => CoveredFrom = Math.Min(CoveredFrom, fromSeq);

    public void AddMessage(long seq, long time, string text)
    {
        if (text.Length == 0 || Contains(seq))
            return;
        // 已载入区间内的追加天然递增(按 seq 回放); 补页则整段插到前面, 由 Merge 处理。
        if (_messages.Count > 0 && _messages[^1].Seq >= seq)
            return;
        _messages.Add(new SessionTimeline.Entry(seq, time, text));
    }

    public void AddTurnStart(long seq)
    {
        if (_turnStarts.Count > 0 && _turnStarts[^1] >= seq)
            return;
        _turnStarts.Add(seq);
    }

    public void AddReverted(long from, long to)
    {
        if (_reverted.Any(range => range.From == from && range.To == to))
            return;
        _reverted.Add((from, to));
        _messages.RemoveAll(entry => entry.Seq >= from && entry.Seq <= to);
    }

    /**
     * 把一批更早的区间合并进来(补页用): 消息/回合起点插到前面, 回退区间并进来。
     * coveredFrom 是这批扫描的**起点**, 不是消息里最小的 seq: 每个回合的开头都先有 turn/start、
     * system/message 之类的事件, 首条用户消息的 seq 必然大于扫描起点(实测 345b074a 首条消息在 seq 7)。
     * 拿 messages[0].Seq 当覆盖起点, CoveredFrom 就永远回不到 0, /timestamp 每次都重扫整段前缀。
     *
     * 按 seq 归并: 调用方可能已经扫过这段(索引先铺满, 补页又送来同一批消息), 直接插会造出重复条目 ——
     * 索引条数虚增之后, "全量消息里的下标"就算不准, 跳转全部落空。
     */
    public void MergeEarlier(
        IReadOnlyList<SessionTimeline.Entry> messages,
        IReadOnlyList<long> turnStarts,
        IReadOnlyList<(long From, long To)> reverted,
        long coveredFrom)
    {
        foreach (var (from, to) in reverted)
            AddReverted(from, to);
        // 只收比现有第一条更早的: 两批都按 seq 递增, 所以这就是"差集"。
        var firstSeq = _messages.Count > 0 ? _messages[0].Seq : long.MaxValue;
        var earlier = messages.Where(entry => entry.Seq < firstSeq && !Contains(entry.Seq)).ToList();
        if (earlier.Count > 0)
            _messages.InsertRange(0, earlier);
        var firstTurn = _turnStarts.Count > 0 ? _turnStarts[0] : long.MaxValue;
        var earlierTurns = turnStarts.Where(seq => seq < firstTurn).ToList();
        if (earlierTurns.Count > 0)
            _turnStarts.InsertRange(0, earlierTurns);
        CoverFrom(coveredFrom);
    }

    /** 这条消息所属回合的 TurnStart 序号: 取索引里第一个 ≤ seq 的回合起点(没有就是 0)。 */
    public long TurnStartOf(long seq)
    {
        var low = 0;
        var high = _turnStarts.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            if (_turnStarts[middle] <= seq)
                low = middle + 1;
            else
                high = middle;
        }
        return low == 0 ? 0 : _turnStarts[low - 1];
    }

    /**
     * 按页扫一段日志, 结果一次给全: 用户消息、回合起点、回退区间, 以及这段扫描的起点。
     * 只在后台线程调用(它按页读盘), 合并到索引必须回到 UI 线程(见 MergeEarlier)。
     */
    public static (List<SessionTimeline.Entry> Messages, List<long> TurnStarts,
        List<(long From, long To)> Reverted, long CoveredFrom)
        Scan(Session session, long fromSeq, long toSeqExclusive, IReadOnlyList<(long From, long To)> knownReverted)
        => OfEvents([.. session.SnapshotEvents(fromSeq, toSeqExclusive)], fromSeq, knownReverted);

    /**
     * 索引只从这三类事件里取东西: 用户消息、回合起点、回退。
     * 用白名单在**解析载荷之前**挡掉其余类型 —— 它们的 Data 是延迟解析的, 逐条解析会把补页成本
     * 压在索引上(百万条会话里 99.7% 是安全决策审计, 与这三类无关)。
     * 这里刻意不用 TranscriptRenderer 的静默表: 那张表说的是"转录上画不出字符", turn/start 也在里面,
     * 而回合起点正是本索引必须收的东西。
     */
    private static readonly HashSet<string> IndexedEventTypes = new(StringComparer.Ordinal)
    {
        SessionEventTypes.UserMessage,
        SessionEventTypes.TurnStart,
        SessionEventTypes.SessionRevert,
    };

    /** 已经读进内存的事件版本: 补页把这一页直接渲染掉, 顺便用它补齐索引, 不再为索引重读一遍。 */
    public static (List<SessionTimeline.Entry> Messages, List<long> TurnStarts,
        List<(long From, long To)> Reverted, long CoveredFrom)
        OfEvents(IReadOnlyList<SessionEvent> events, long fromSeq, IReadOnlyList<(long From, long To)> knownReverted)
    {
        var messages = new List<SessionTimeline.Entry>();
        var turnStarts = new List<long>();
        var reverted = new List<(long From, long To)>();
        foreach (var sessionEvent in events)
        {
            if (!IndexedEventTypes.Contains(sessionEvent.Type))
                continue;
            switch (sessionEvent.Data)
            {
                case SessionRevertPayload revert:
                    reverted.Add((revert.TargetSeq, sessionEvent.Seq));
                    break;
                case TurnStartPayload:
                    turnStarts.Add(sessionEvent.Seq);
                    break;
            }
            var text = SessionTimeline.UserText(sessionEvent);
            if (text.Length > 0)
                messages.Add(new SessionTimeline.Entry(sessionEvent.Seq, sessionEvent.Time, text));
        }
        // 回退可能出现在消息之后, 也可能落在这一页之外(尾窗里已知的那几条): 两处都要滤。
        var ranges = new List<(long From, long To)>(knownReverted);
        ranges.AddRange(reverted);
        messages.RemoveAll(entry => SessionTimeline.Contains(ranges, entry.Seq));
        return (messages, turnStarts, reverted, fromSeq);
    }
}

