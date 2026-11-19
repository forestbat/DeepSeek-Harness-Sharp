using System.Collections;
using Dsh.Llm;

namespace Dsh.Core;

/** 前缀页的读取回调: 返回 [offset, offset + count) 的事件; 读不到返回空列表。 */
public delegate IReadOnlyList<SessionEvent> SessionEventPageReader(long offset, int count);

/**
 * 窗口化恢复的输入: 常驻尾窗 + 前缀页读取回调。
 *
 * Resident 必须从 ResidentFrom 开始、seq 连续到 TotalEvents; TotalEvents 是日志里的事件总数。
 * ReadPage 为空时前缀不可得, 取前缀事件会得到 null(缺页), 而不是伪造事件。
 */
public readonly record struct SessionWindow(
    long ResidentFrom,
    long TotalEvents,
    IReadOnlyList<SessionEvent> Resident,
    SessionEventPageReader? ReadPage);

/** 前缀页读不出来时的失败: 数据还在盘上, 只是这次没取到。 */
public sealed class SessionPageUnavailableException(long seq, long page)
    : InvalidOperationException($"session event {seq} is not resident and page {page} could not be read back")
{
    public long Seq { get; } = seq;

    public long Page { get; } = page;
}

public sealed class Session
{
    private SessionEvent[] _buffer = [];
    /** 常驻尾窗的事件数; 总事件数是 _residentFrom + _count。 */
    private int _count;
    /** 常驻尾窗的起点 seq: 非窗口会话恒为 0。 */
    private long _residentFrom;
    private PageCache? _pages;
    private SessionLog _snapshot = SessionLog.Empty;
    private readonly Lock _writeGate = new();
    private Surface.Manager _surfaceManager;
    private EpochHeader? _headerFold;
    private RequestContextPayload? _contextFold;
    private string? _workingDirectoryFold;
    private List<Message> _derived = [];
    private int _derivedNodes;
    private int _derivedGeneration;

    public event Action<Session, SessionEvent>? Appended;

    public event Action<Session, long>? Truncated;

    public event Action<Session, SessionHeader>? Renamed;

    public SessionHeader Header { get; private set; }

    public SessionId Id => Header.Id;

    /** Effective working directory: the latest working-directory change, else the header cwd (which stays the write permission root). */
    public string? WorkingDirectory => Volatile.Read(ref _workingDirectoryFold) ?? Header.Cwd;

    public long InheritedEventCount { get; }

    public long FirstLiveSeq { get; }

    /** 常驻尾窗的起点 seq: 更早的事件按页从持久化读。 */
    public long ResidentFrom => _residentFrom;

    /** 累计的页读取次数(打点用)。 */
    public long PageFaults => _pages?.Reads ?? 0;

    public Surface.Manager SurfaceManager => _surfaceManager;

    /** 前缀页的事件条数: 页边界就是 seq / PageSize。 */
    internal const int PageSize = 4096;

    private Session(
        SessionId id,
        IReadOnlyList<SessionEvent>? seed,
        SessionHeader? header,
        long? suppliedInheritedEventCount,
        SessionWindow? window = null)
    {
        _residentFrom = window?.ResidentFrom ?? 0;
        // 窗口以外的事件按页取: 跨边界 replace 因此不必要求窗口覆盖到远端节点(见 B5)。
        _surfaceManager = new Surface.Manager(() => Volatile.Read(ref _snapshot), _residentFrom,
            seq => _pages?.Get(seq));
        if (window is { } loaded)
        {
            _pages = loaded.ReadPage is null ? null : new PageCache(loaded.ReadPage);
            // 先发布一个"只有前缀、尾窗为空"的全范围视图: 折叠状态机据此知道下一条事件该是 ResidentFrom。
            Volatile.Write(ref _snapshot, new SessionLog(_buffer, 0, _residentFrom, _pages));
            if (loaded.Resident.Count > 0 && loaded.Resident[0].Seq != _residentFrom)
                throw new ArgumentException(
                    $"window resident events start at seq {loaded.Resident[0].Seq}, expected {_residentFrom}");
            foreach (var residentEvent in loaded.Resident)
            {
                if (residentEvent.Seq != _residentFrom + _count)
                    throw new ArgumentException(
                        $"window resident event {residentEvent.Seq} is not contiguous at offset {_count}");
                try
                {
                    _surfaceManager.ValidateNext(residentEvent);
                }
                catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException)
                {
                    throw new ArgumentException($"invalid window event at seq {residentEvent.Seq}: {error.Message}");
                }
                Commit(residentEvent);
            }
            if (_residentFrom + _count != loaded.TotalEvents)
                throw new ArgumentException(
                    $"window covers up to seq {_residentFrom + _count}, but the log holds {loaded.TotalEvents} events");
        }
        if (seed is not null)
        {
            for (var index = 0; index < seed.Count; index++)
            {
                var seedEvent = seed[index];
                if (seedEvent.Seq != index)
                {
                    throw new ArgumentException(
                        $"seed event at index {index} has seq {seedEvent.Seq} (expected {index}); seed must be contiguous from 0");
                }
                try
                {
                    _surfaceManager.ValidateNext(seedEvent);
                }
                catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException)
                {
                    throw new ArgumentException($"invalid seed event at index {index}: {error.Message}");
                }
                Commit(seedEvent);
            }
        }
        // 窗口会话的尾窗已是"历史", 新的自有事件从总数之后开始。
        FirstLiveSeq = TotalCount;
        Header = header is null
            ? new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = id,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsSeeded = false,
            }
            : header;
        Header.Validate();
        if (Header.Id != id)
            throw new ArgumentException($"session header id \"{Header.Id}\" does not match session id \"{id}\"");
        if (Header.IsSeeded && seed is null && window is null)
            throw new ArgumentException("seeded session requires an explicit constructor seed");
        if (Header.IsSeeded && suppliedInheritedEventCount is null)
            throw new ArgumentException("seeded session requires an inherited event count");
        var inheritedEventCount = suppliedInheritedEventCount ?? 0;
        if (!Header.IsSeeded && inheritedEventCount != 0)
            throw new ArgumentException("unseeded session inherited event count must be 0");
        if (inheritedEventCount > TotalCount)
            throw new ArgumentException("session inherited event count exceeds its event log");
        InheritedEventCount = inheritedEventCount;
        if ((seed is not null || window is not null)
            && (_count == 0 || _buffer[_count - 1].Type != SessionEventTypes.SessionEndSeed))
        {
            Append(new SessionEndSeedPayload());
        }
    }

    public static Session Create(SessionId id, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
        => new(id, seed, header, inheritedEventCount);

    public static Session FromRestore(SessionId id, IReadOnlyList<SessionEvent> seed, SessionHeader header, long inheritedEventCount)
        => new(id, seed, header, inheritedEventCount);

    /** 窗口化恢复: 前缀按页读, 尾窗常驻。折叠范围是 [window.ResidentFrom, TotalEvents)。 */
    public static Session FromWindow(SessionId id, SessionWindow window, SessionHeader header, long inheritedEventCount)
        => new(id, null, header, inheritedEventCount, window);

    /** 总事件数(尾窗之前的那些不在内存里)。 */
    private long TotalCount => _residentFrom + _count;

    public SessionEvent? EventAt(long seq)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return seq >= 0 && seq < snapshot.Total ? snapshot.EventAt(seq) : null;
    }

    /**
     * 从最新一条往前找第一个满足条件的事件, 找到即停; 只看常驻区间, 找不到返回 null。
     *
     * 窗口会话里 ResidentFrom 之前的事件要按页从日志读回来(一页 4096 条, 常常还是 1 帧 1 条),
     * "往前扫到 seq 0"这种取值在窗口化打开之后就等于把整份日志重新读一遍。
     * 恢复路径上按"最后一条用户活动/最后一次策略/最后一个回合"取值的调用方一律走这里。
     */
    public SessionEvent? LastResidentEventWhere(Func<SessionEvent, bool> predicate)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        for (var seq = snapshot.Total - 1; seq >= _residentFrom; seq -= 1)
        {
            if (snapshot.EventAt(seq) is { } sessionEvent && predicate(sessionEvent))
                return sessionEvent;
        }
        return null;
    }

    public IReadOnlyList<SessionEvent> SnapshotEvents(long fromSeq = 0, long? toSeqExclusive = null)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var to = toSeqExclusive ?? snapshot.Total;
        if (fromSeq == 0 && to == snapshot.Total)
            return snapshot;
        if (fromSeq < 0 || to > snapshot.Total || fromSeq > to)
            throw new ArgumentOutOfRangeException(
                nameof(fromSeq),
                $"invalid snapshot range [{fromSeq}, {to}) for {snapshot.Total} events");
        return snapshot.Slice((int)fromSeq, (int)to);
    }

    /**
     * 常驻尾窗的事件视图: [ResidentFrom, Seq), 不触发前缀缺页读。
     *
     * 只需要"最后一条/尾部一段"的调用方(取最后一条事件、折叠最后发布的摘要、截取尾部文本)用它,
     * 而不是 SnapshotEvents() —— 后者在窗口会话里会一页一页把整份日志读回来。
     */
    public IReadOnlyList<SessionEvent> ResidentEvents()
        => _residentFrom == 0 ? SnapshotEvents() : SnapshotEvents(_residentFrom);

    public IReadOnlyList<SessionEvent> OwnEvents() => SnapshotEvents(InheritedEventCount);

    public long Seq => Volatile.Read(ref _snapshot).Total;

    /** 就地截断到 eventCount: 只剩 [0, eventCount), 重建 surface 与 header/context 折叠。仅限本会话自有区间。 */
    public bool TryTruncateTo(long eventCount)
    {
        long truncatedTo;
        lock (_writeGate)
        {
            if (eventCount < InheritedEventCount || eventCount > TotalCount)
                return false;
            if (eventCount == TotalCount)
                return true;
            // 窗口会话的前缀不在内存里, 就地回退无法重建那一段的折叠状态。
            if (eventCount < _residentFrom)
                return false;
            _count = (int)(eventCount - _residentFrom);
            _pages?.InvalidateFrom(_residentFrom + _count);
            Volatile.Write(ref _snapshot, new SessionLog(_buffer, _count, _residentFrom, _pages));
            // surface 与折叠状态由事件日志派生: 日志缩短后整体重建, 不能就地回退(见 D3 revert)。
            _surfaceManager = new Surface.Manager(() => Volatile.Read(ref _snapshot), _residentFrom);
            _headerFold = null;
            _contextFold = null;
            _workingDirectoryFold = null;
            for (var index = 0; index < _count; index++)
                FoldAggregates(_buffer[index]);
            _derived = [];
            _derivedNodes = 0;
            _derivedGeneration = -1;
            truncatedTo = TotalCount;
        }

        NotifyTruncated(truncatedTo);
        return true;
    }

    private void NotifyTruncated(long eventCount)
    {
        var subscribers = Truncated;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            if (subscriber is not Action<Session, long> action)
                continue;
            try
            {
                action(this, eventCount);
            }
            catch
            {
                // Observer failures are contained and never unmake a committed truncation.
            }
        }
    }

    public SessionEvent Append(SessionEventPayload payload, SurfaceOp? surfaceOp = null, IReadOnlyList<long>? sourceEventSeqs = null)
    {
        SessionEvent sessionEvent;
        Action<Session, SessionEvent>? subscribers;
        lock (_writeGate)
        {
            sessionEvent = new SessionEvent
            {
                Type = payload.Type,
                Seq = TotalCount,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = payload,
                Ignorable = SessionEventCodec.IsIgnorable(payload.Type),
                SurfaceOp = surfaceOp,
                SourceEventSeqs = sourceEventSeqs,
            };
            _surfaceManager.ValidateNext(sessionEvent);
            Commit(sessionEvent);
            subscribers = Appended;
        }
        if (subscribers is not null)
        {
            foreach (var subscriber in subscribers.GetInvocationList())
            {
                if (subscriber is not Action<Session, SessionEvent> action)
                    continue;
                try
                {
                    action(this, sessionEvent);
                }
                catch
                {
                    // Observer failures are contained and never unmake a committed append.
                }
            }
        }
        return sessionEvent;
    }

    public void Rename(string title)
    {
        Header = Header with { Title = title };
        var subscribers = Renamed;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            if (subscriber is not Action<Session, SessionHeader> action)
                continue;
            try
            {
                action(this, Header);
            }
            catch
            {
                // Observer failures are contained and never unmake a rename.
            }
        }
    }

    public EpochHeader? RequestHeader() => Volatile.Read(ref _headerFold);

    public RequestContextPayload? RequestContext() => Volatile.Read(ref _contextFold);

    public IReadOnlyList<Message> DeriveMessages()
    {
        var nodes = _surfaceManager.Nodes;
        var generation = _surfaceManager.ReplaceGeneration;
        if (generation != _derivedGeneration)
        {
            _derived = [];
            _derivedNodes = 0;
            _derivedGeneration = generation;
        }
        var snapshot = Volatile.Read(ref _snapshot);
        for (var index = _derivedNodes; index < nodes.Count; index++)
        {
            // 节点落在前缀里且取不回来(缺页)时跳过这一条: 少一条消息比凭空造一条好。
            if (snapshot.EventAt(nodes[index]) is not { } sessionEvent)
                continue;
            var message = Surface.DeriveEventMessage(sessionEvent);
            if (message is not null)
                _derived.Add(message);
        }
        _derivedNodes = nodes.Count;
        return [.. _derived];
    }

    private void Commit(SessionEvent sessionEvent)
    {
        EnsureCapacity(_count + 1);
        _buffer[_count] = sessionEvent;
        _count++;
        FoldAggregates(sessionEvent);
        Volatile.Write(ref _snapshot, new SessionLog(_buffer, _count, _residentFrom, _pages));
    }

    private void FoldAggregates(SessionEvent sessionEvent)
    {
        switch (sessionEvent.Data)
        {
            case RequestHeaderPayload header:
                Volatile.Write(ref _headerFold, Core.RequestHeader.Canonicalize(header.Header));
                break;
            case RequestContextPayload context:
                Volatile.Write(ref _contextFold, context);
                break;
            case WorkingDirectoryChangedPayload change:
                Volatile.Write(ref _workingDirectoryFold, change.Cwd);
                break;
        }
    }

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
            return;
        var capacity = _buffer.Length == 0 ? 16 : _buffer.Length;
        while (capacity < required)
            capacity *= 2;
        Array.Resize(ref _buffer, capacity);
    }

    /**
     * 前缀页的 LRU: 固定 4096 事件一页, 最多 8 页常驻。
     * 读失败的页记成"不可得"(null), 取它的事件得到 null(缺页), 不重试也不伪造。
     */
    internal sealed class PageCache(SessionEventPageReader reader)
    {
        private const int MaxPages = 8;

        private readonly Lock _gate = new();
        private readonly Dictionary<long, SessionEvent[]?> _pages = [];
        private readonly List<long> _order = [];
        private long _reads;

        public long Reads => Interlocked.Read(ref _reads);

        public SessionEvent? Get(long seq)
        {
            var page = seq / Session.PageSize;
            lock (_gate)
            {
                if (_pages.TryGetValue(page, out var cached))
                {
                    if (cached is null)
                        return null;
                    TouchLocked(page);
                    return At(cached, seq);
                }
            }
            var loaded = Load(page);
            lock (_gate)
            {
                _pages[page] = loaded;
                _order.Add(page);
                while (_order.Count > MaxPages)
                {
                    var evicted = _order[0];
                    _order.RemoveAt(0);
                    _pages.Remove(evicted);
                }
                return loaded is null ? null : At(loaded, seq);
            }
        }

        public void InvalidateFrom(long seq)
        {
            var page = seq / Session.PageSize;
            lock (_gate)
            {
                foreach (var key in _pages.Keys.Where(key => key >= page).ToList())
                {
                    _pages.Remove(key);
                    _order.Remove(key);
                }
            }
        }

        private static SessionEvent? At(SessionEvent[] page, long seq)
        {
            var offset = (int)(seq % Session.PageSize);
            return offset < page.Length ? page[offset] : null;
        }

        private SessionEvent[]? Load(long page)
        {
            Interlocked.Increment(ref _reads);
            try
            {
                var events = reader(page * Session.PageSize, Session.PageSize);
                if (events.Count == 0)
                    return null;
                var stored = new SessionEvent[events.Count];
                for (var index = 0; index < events.Count; index += 1)
                    stored[index] = events[index];
                return stored;
            }
            catch (Exception error) when (error is IOException or FormatException or InvalidOperationException)
            {
                return null;
            }
        }

        private void TouchLocked(long page)
        {
            _order.Remove(page);
            _order.Add(page);
        }
    }

    // 单写者追加、发布不可变 (数组, 长度, 页) 视图: 读者取一次引用后无锁遍历, 元素先写、长度后发布, 故不会读到未写入的槽。
    internal sealed class SessionLog : IReadOnlyList<SessionEvent>
    {
        public static readonly SessionLog Empty = new([], 0, 0, null, 0, 0);

        private readonly SessionEvent[] _tail;
        private readonly int _tailCount;
        private readonly long _residentFrom;
        private readonly PageCache? _pages;
        private readonly long _from;
        private readonly long _to;

        public SessionLog(SessionEvent[] tail, int tailCount, long residentFrom, PageCache? pages)
            : this(tail, tailCount, residentFrom, pages, 0, residentFrom + tailCount)
        {
        }

        private SessionLog(SessionEvent[] tail, int tailCount, long residentFrom, PageCache? pages, long from, long to)
        {
            _tail = tail;
            _tailCount = tailCount;
            _residentFrom = residentFrom;
            _pages = pages;
            _from = from;
            _to = to;
        }

        public int Count => (int)(_to - _from);

        /** 整个日志的事件总数(与视图范围无关)。 */
        public long Total => _residentFrom + _tailCount;

        public SessionEvent this[int index]
        {
            get
            {
                var seq = _from + index;
                return EventAt(seq) ?? throw new SessionPageUnavailableException(seq, seq / Session.PageSize);
            }
        }

        public SessionEvent? EventAt(long seq)
        {
            if (seq < _residentFrom)
                return _pages?.Get(seq);
            var offset = seq - _residentFrom;
            return offset < _tailCount ? _tail[offset] : null;
        }

        public SessionLog Slice(int from, int to) => new(_tail, _tailCount, _residentFrom, _pages, _from + from, _from + to);

        public IEnumerator<SessionEvent> GetEnumerator()
        {
            for (var index = 0; index < Count; index++)
                yield return this[index];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
