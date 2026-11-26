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
    /** 已被回退事件收掉的日志区间(闭区间): 这些事件还在日志里, 但不再属于会话。 */
    private readonly List<(long From, long To)> _reverted = [];
    /** 发号凭证: 有它时序号由日志说话, 而不是本进程内存里的计数(多宿主写同一份日志的前提)。 */
    private ISessionSequencer? _sequencer;
    /** 已知落盘的条数; 之后的都是已发号但还没落盘的自有事件, 必要时可整段重编号。 */
    private long _durableCount;
    /** 上次写下折叠检查点的页号(-1 = 还没写过: 历史会话恢复后的第一次页边界就写一份)。 */
    private long _checkpointPage = -1;

    public event Action<Session, SessionEvent>? Appended;


    /** 追加了一条回退事件(参数是回退目标序号): 从日志派生的辅助状态(收件箱、时间线)据此重建。 */
    public event Action<Session, long>? Reverted;

    /** 未落盘的那一段整体换了号(参数: 从哪个序号起、平移量): 客户端据此把卡片按新号对齐或直接重放。 */
    public event Action<Session, long, long>? Renumbered;

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

    /** 读过多少不同的页: 与 PageFaults 的比值就是重复解析的倍数(折前缀时应当接近 1)。 */
    public long PageFaultDistinct => _pages?.DistinctPages ?? 0;

    public Surface.Manager SurfaceManager => _surfaceManager;

    /** 前缀页的事件条数: 页边界就是 seq / PageSize。 */
    internal const int PageSize = 4096;

    private Session(
        SessionId id,
        IReadOnlyList<SessionEvent>? seed,
        SessionHeader? header,
        long? suppliedInheritedEventCount,
        SessionWindow? window = null,
        SurfaceFoldSnapshot? adoptedSurface = null)
    {
        _residentFrom = window?.ResidentFrom ?? 0;
        // 表面**必须覆盖整条日志**: 它就是发给模型的那份消息序列(Session.DeriveMessages),
        // 窗口只决定"哪些事件对象常驻内存", 前缀按页取。少折前缀等于让模型丢失窗口之前的上下文,
        // 压缩也就只能压到窗口内那一段。命中派生缓存时不必再折一遍(见 adoptedSurface)。
        _surfaceManager = new Surface.Manager(() => Volatile.Read(ref _snapshot), 0,
            seq => _pages?.Get(seq), _residentFrom, adoptedSurface);
        // 折叠检查点带来的请求头/上下文: 尾窗里往往没有这两条(最近一次请求在窗口之外),
        // 带上它们就不必为了取值把整份前缀扫一遍。尾窗里若另有更新的同类事件, 下面的 Commit 会覆盖。
        _headerFold = adoptedSurface?.RequestHeader;
        _contextFold = adoptedSurface?.RequestContext;
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

    /** 窗口化恢复: 前缀按页读, 尾窗常驻。折叠范围是整条日志(见 ctor); 命中派生缓存时直接采用折叠结果。 */
    public static Session FromWindow(
        SessionId id,
        SessionWindow window,
        SessionHeader header,
        long inheritedEventCount,
        SurfaceFoldSnapshot? adoptedSurface = null)
        => new(id, null, header, inheritedEventCount, window, adoptedSurface);

    /**
     * 表面折叠的派生缓存快照: 折叠已经跟上日志末尾时才有意义(否则存下来的是半截状态)。
     * LogLength 由持久化层在落盘时填(只有它知道文件长度)。
     */
    public SurfaceFoldSnapshot? FoldSnapshot()
    {
        // 先看折叠进度再碰 Nodes: 取 Nodes 会触发"把整条日志折一遍", 那是加载路径上最贵的一步。
        if (_surfaceManager.FoldedThrough + 1 != TotalCount)
            return null;
        return new SurfaceFoldSnapshot(
            [.. _surfaceManager.Nodes], 0, TotalCount, _surfaceManager.ReplaceGeneration,
            RequestHeader(), RequestContext());
    }

    /**
     * 折叠进度跨过一个读页边界之后才值得写一次检查点。
     *
     * 代价按页摊: 一页 4096 条事件, 写一次检查点约一次 fsync; 崩溃时最多重读一页。
     * 还没折到日志末尾时返回 null —— 半截状态存下来没有意义。
     */
    public SurfaceFoldSnapshot? FoldSnapshotIfDue()
    {
        if (_surfaceManager.FoldedThrough + 1 != TotalCount)
            return null;
        var page = _surfaceManager.FoldedThrough / PageSize;
        if (page <= _checkpointPage)
            return null;
        _checkpointPage = page;
        return FoldSnapshot();
    }

    /** 收尾时补写一次: 把最后一个不满页也盖上。 */
    public SurfaceFoldSnapshot? FoldSnapshotOnClose() => FoldSnapshot();

    /** 完整性不变量: 表面必须已折到日志末尾。测试与基准用它证明"没有少一截"。 */
    public void AssertFullyFolded()
    {
        _ = _surfaceManager.Nodes;
        if (_surfaceManager.FoldedThrough + 1 != TotalCount)
            throw new InvalidOperationException(
                $"surface folded through {_surfaceManager.FoldedThrough} but the session holds {TotalCount} event(s)");
    }

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
        => LastEventWhere(_residentFrom, predicate);

    /**
     * 只看**本会话自己的**事件: 前 InheritedEventCount 条是从别的会话继承来的(fork / 续接的种子)。
     *
     * 属于会话身份的判断(例如"这个会话里被总是允许过的工具")不能用继承来的历史 —— 否则 fork 一份会话就顺带
     * 继承了父会话的授权; 而同一会话的恢复要认得自己此前的授权, 因此下界是 InheritedEventCount 而不是 FirstLiveSeq。
     * 仍然只看常驻尾窗: 授权落在尾窗之外就再问一次, 方向是更保守而不是更松。
     */
    public SessionEvent? LastOwnEventWhere(Func<SessionEvent, bool> predicate)
        => LastEventWhere(Math.Max(_residentFrom, InheritedEventCount), predicate);

    private SessionEvent? LastEventWhere(long fromSeq, Func<SessionEvent, bool> predicate)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        for (var seq = snapshot.Total - 1; seq >= fromSeq; seq -= 1)
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


    /**
     * 回退到 targetSeq 之前: **只追加**一条 `session/revert`, 不缩短日志(阶段 4a)。
     *
     * 撤销从不物理截断: 正在读旧区间的读者不会因为文件变短而看到不一致的视图,
     * 派生缓存(日志长度/条数只增)也继续有效。表面上被收掉的那一段由这条事件自己描述
     * (SourceEventSeqs + 覆盖同一区间的 SurfaceOp), 任何读者折到它都会得到同一个结果。
     *
     * 只允许回退本会话自有区间(forks 的种子前缀不可回退); 越界返回 null。
     */
    public SessionEvent? Revert(long targetSeq, string? reason = null)
    {
        if (targetSeq < InheritedEventCount || targetSeq > TotalCount)
            return null;
        var payload = new SessionRevertPayload(targetSeq, reason);
        var nodes = _surfaceManager.Nodes;
        var shadowed = nodes.Where(node => node >= targetSeq).ToList();
        var appended = shadowed.Count == 0
            ? Append(payload)
            : Append(payload, new SurfaceOp.Replace(shadowed[0], shadowed[^1]), shadowed);
        var subscribers = Reverted;
        if (subscribers is null)
            return appended;
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            if (subscriber is not Action<Session, long> action)
                continue;
            try
            {
                action(this, targetSeq);
            }
            catch
            {
                // Observer failures are contained and never unmake a committed revert.
            }
        }
        return appended;
    }

    /**
     * 这个序号是否已被某条回退事件收掉。
     * 回退只追加, 所以"当前会话包含什么"不能再靠"日志里有什么"判断 —— 从日志扫描派生状态的代码(收件箱、
     * 时间线、交付物)必须同时问一句这里。
     */
    public bool IsReverted(long seq)
    {
        lock (_writeGate)
        {
            foreach (var (from, to) in _reverted)
            {
                if (seq >= from && seq <= to)
                    return true;
            }
            return false;
        }
    }

    /** 已被回退的日志区间(闭区间), 按日志顺序。 */
    public IReadOnlyList<(long From, long To)> RevertedRanges
    {
        get
        {
            lock (_writeGate)
                return [.. _reverted];
        }
    }

    /**
     * 接上发号凭证(由持久化接线在创建/接管写句柄时调用)。
     *
     * 接上之后的第一次追加会先向凭证要号: 日志上多出来的事件(别的宿主写进来的)先按日志读回来,
     * 自己的号被别人的票覆盖过则整段重编号 —— 号永远以日志为准, 内存里的计数只是它的影子。
     */
    public void AttachSequencer(ISessionSequencer? sequencer)
    {
        lock (_writeGate)
        {
            _sequencer = sequencer;
            _durableCount = TotalCount;
        }
    }


    /**
     * 把内存对齐到凭证给出的下一个号。
     *
     * - 号在内存之后: 那一段是别的宿主写的, 按日志读回来折进表面(少折等于让模型丢上下文);
     * - 号在内存之前: 我们的票被别人接管过, 已发号未落盘的那一段按新起点整段重编号。
     */
    private void ApplySequencerLocked(long next)
    {
        if (next > TotalCount)
        {
            foreach (var sessionEvent in _sequencer!.ReadRange(TotalCount, next))
            {
                if (sessionEvent.Seq != TotalCount)
                    continue;
                _surfaceManager.ValidateNext(sessionEvent);
                Commit(sessionEvent);
            }
            _durableCount = Math.Max(_durableCount, TotalCount);
            return;
        }
        if (next < TotalCount && next >= (_sequencer?.DurableCount ?? _durableCount))
            RebaseUnsaved(next);
    }

    /**
     * 号被别的写者接管: 把未落盘的那一段按日志真实条数重新编号, 并返回重编号后的这一段。
     *
     * 已广播出去的号只能整体平移并广播这次平移(见 SessionRenumberNotification): 客户端的卡片按新号对齐,
     * 不这么做就等于让界面拿着一套日志里不存在的号。
     */
    public IReadOnlyList<SessionEvent> RebaseUnsaved(long newStart)
    {
        List<SessionEvent> rebased = [];
        long delta;
        long fromSeq;
        lock (_writeGate)
        {
            fromSeq = Math.Clamp(_sequencer?.DurableCount ?? _durableCount, 0, TotalCount);
            if (fromSeq < _residentFrom)
            {
                // 窗口会话的前缀不在内存里: 在这里重编号等于凭空改写别人的历史, 交给调用方报错。
                return [];
            }
            // 先把未落盘的那一段摘出来(它们要换号), 再把内存缩短到已落盘处。
            var unsaved = new List<SessionEvent>();
            for (var offset = (int)(fromSeq - _residentFrom); offset < _count; offset += 1)
                unsaved.Add(_buffer[offset]);
            TruncateMemoryLocked(fromSeq);
            // 别人占了我们要用的号: 那几条按日志读回来当成历史, 否则内存会在中间缺一块。
            if (newStart > fromSeq && _sequencer is { } sequencer)
            {
                foreach (var sessionEvent in sequencer.ReadRange(fromSeq, newStart))
                {
                    if (sessionEvent.Seq != TotalCount)
                        continue;
                    _surfaceManager.ValidateNext(sessionEvent);
                    Commit(sessionEvent);
                }
            }
            delta = newStart - fromSeq;
            // 我们这一段整体平移到 newStart 之后, 一条都不丢。
            foreach (var stale in unsaved)
            {
                var shifted = ShiftEvent(stale, delta, fromSeq);
                _surfaceManager.ValidateNext(shifted);
                Commit(shifted);
                rebased.Add(shifted);
            }
        }
        if (rebased.Count > 0 && delta != 0)
            PublishRenumber(fromSeq, delta);
        return rebased;
    }

    /** 把内存缩短到 keep 条并重建表面/聚合(重编号与参考实现共用)。 */
    private void TruncateMemoryLocked(long keep)
    {
        _count = (int)(keep - _residentFrom);
        _pages?.InvalidateFrom(keep);
        Volatile.Write(ref _snapshot, new SessionLog(_buffer, _count, _residentFrom, _pages));
        _surfaceManager = new Surface.Manager(() => Volatile.Read(ref _snapshot), 0, seq => _pages?.Get(seq), _residentFrom);
        // 新建的管理器什么都不知道: 已落盘的那一段要按新号重新折一遍。
        _surfaceManager.Refold(seq => Volatile.Read(ref _snapshot).EventAt(seq), 0, keep);
        _headerFold = null;
        _contextFold = null;
        _workingDirectoryFold = null;
        _reverted.Clear();
        for (var index = 0; index < _count; index += 1)
            FoldAggregates(_buffer[index]);
        _derived = [];
        _derivedNodes = 0;
        _derivedGeneration = -1;
    }

    private void PublishRenumber(long fromSeq, long delta)
    {
        var subscribers = Renumbered;
        if (subscribers is null)
            return;
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            if (subscriber is not Action<Session, long, long> action)
                continue;
            try
            {
                action(this, fromSeq, delta);
            }
            catch
            {
                // 观察者失败不能回滚已经重编号的状态。
            }
        }
    }

    /** 重编号: 序号整体平移, 指向这段之后的来源清单一起平移, 指向更早事件的保持不变。 */
    private static SessionEvent ShiftEvent(SessionEvent source, long delta, long firstUnsaved)
    {
        var sources = source.SourceEventSeqs is { } raw
            ? raw.Select(seq => seq >= firstUnsaved ? seq + delta : seq).ToList()
            : null;
        return new SessionEvent
        {
            Type = source.Type,
            Seq = source.Seq + delta,
            Time = source.Time,
            Data = source.Data,
            Ignorable = source.Ignorable,
            SurfaceOp = source.SurfaceOp,
            SourceEventSeqs = sources,
        };
    }

    public SessionEvent Append(SessionEventPayload payload, SurfaceOp? surfaceOp = null, IReadOnlyList<long>? sourceEventSeqs = null)
    {
        SessionEvent sessionEvent;
        Action<Session, SessionEvent>? subscribers;
        lock (_writeGate)
        {
            // 号必须在创建事件之前定下来: 广播早于落盘, 而广播出去的号不能事后改。
            if (_sequencer is { } sequencer)
                ApplySequencerLocked(sequencer.NextSeq());
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

    /**
     * 补回折叠时的请求头/请求上下文(旧检查点里没有这两项时由调用方从日志里找回来)。
     *
     * 只填当前为空的那一半: 快照里已有的不能被更旧的日志值覆盖。
     */
    public void AdoptFoldMeta(EpochHeader? header, RequestContextPayload? context)
    {
        if (header is not null && Volatile.Read(ref _headerFold) is null)
            Volatile.Write(ref _headerFold, Core.RequestHeader.Canonicalize(header));
        if (context is not null && Volatile.Read(ref _contextFold) is null)
            Volatile.Write(ref _contextFold, context);
    }

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
            // 回退区间与"会话里还剩什么"一起由日志派生: 重建折叠状态时(截断/载入)必须重算, 否则会漏掉或留住旧区间。
            case SessionRevertPayload revert:
                _reverted.Add((revert.TargetSeq, sessionEvent.Seq));
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
        private readonly Dictionary<long, Page> _pages = [];
        private readonly List<long> _order = [];
        private long _reads;
        private long _distinct;
        /** 已经读过的页: 只用来区分"重复解析"与"第一次读"。 */
        private readonly HashSet<long> _seen = [];
        /**
         * 折叠从前往后读前缀, 而 replace 的 SourceEventSeqs 会随机回看更早的事件 ——
         * 那些回看如果和顺序页共用同一个 LRU, 就会把"下一章要读的页"挤出去, 同一页被反复解压(实测 1.47×)。
         * 于是把顺序读到的最后一页单独钉住: 它不参与淘汰, 换页时才把它并入 LRU。
         */
        private long _pinnedPage = -1;
        private Page _pinned = Page.Unavailable(0);

        public long Reads => Interlocked.Read(ref _reads);

        /** 真正读过多少不同的页(reads 与它的比值就是重复解析的倍数)。 */
        public long DistinctPages => Interlocked.Read(ref _distinct);

        public SessionEvent? Get(long seq)
        {
            var page = seq / Session.PageSize;
            lock (_gate)
            {
                if (page == _pinnedPage)
                    return At(_pinned, seq);
                if (_pages.TryGetValue(page, out var cached))
                {
                    TouchLocked(page);
                    return At(cached, seq);
                }
            }
            var loaded = Load(page);
            lock (_gate)
            {
                // 顺序前进(或第一次读): 钉住这一页, 上一页并进 LRU。
                if (_seen.Add(page))
                    Interlocked.Increment(ref _distinct);
                if (page >= _pinnedPage)
                {
                    if (_pinnedPage >= 0)
                        AddLocked(_pinnedPage, _pinned);
                    _pinnedPage = page;
                    _pinned = loaded;
                    return At(loaded, seq);
                }
                AddLocked(page, loaded);
                return At(loaded, seq);
            }
        }

        /** 放进 LRU 并淘汰最旧的一页。 */
        private void AddLocked(long page, Page loaded)
        {
            _pages[page] = loaded;
            _order.Add(page);
            while (_order.Count > MaxPages)
            {
                var evicted = _order[0];
                _order.RemoveAt(0);
                _pages.Remove(evicted);
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
                if (_pinnedPage >= page)
                {
                    _pinnedPage = -1;
                    _pinned = Page.Unavailable(page * Session.PageSize);
                }
            }
        }

        /**
         * 页就是"从 From 起的连续事件": 页读回来的第一条未必是请求的那一条(记录缺失/被跳过的回退块都会让页从更晚的号开始),
         * 因此下标基准必须是页自己的 From, 不能是 page * PageSize —— 用后者会把整页事件错位一格, 界面上就是"只剩前半截"。
         */
        private readonly record struct Page(long From, SessionEvent[]? Events)
        {
            public static Page Unavailable(long from) => new(from, null);
        }

        private static SessionEvent? At(Page page, long seq)
        {
            if (page.Events is not { } events)
                return null;
            var offset = seq - page.From;
            return offset >= 0 && offset < events.Length ? events[offset] : null;
        }

        private Page Load(long page)
        {
            var start = page * Session.PageSize;
            Interlocked.Increment(ref _reads);
            try
            {
                var events = reader(start, Session.PageSize);
                if (events.Count == 0)
                    return Page.Unavailable(start);
                var stored = new SessionEvent[events.Count];
                for (var index = 0; index < events.Count; index += 1)
                    stored[index] = events[index];
                return new Page(stored[0].Seq, stored);
            }
            catch (Exception error) when (error is IOException or FormatException or InvalidOperationException)
            {
                return Page.Unavailable(start);
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
