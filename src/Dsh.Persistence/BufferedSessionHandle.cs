using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

/**
 * 会话句柄的共同实现: 缓冲、攒批窗口、发号、游标对账、stale 重排、落后补齐、关闭前补写。
 *
 * 两个后端的分歧只有"这一批事件落到哪里": JSONL 写日志文件, 数据库交给提交道。
 * 序号语义(号先定再广播)、缓冲与时间窗、别人的号被用掉时整体重排、库里落后时整段补写,
 * 在这里写一次 —— 以前它们在两个句柄里各写了一遍。
 */
internal abstract class BufferedSessionHandle : ISessionHandle, ISessionSequencer
{
    private readonly Lock _gate = new();
    private readonly List<SessionEvent> _buffered = [];
    private Timer? _timer;
    private bool _closed;
    /** 上一次落盘没成功: 之后每条事件都先重试一次落盘。 */
    private bool _stalled;
    private SessionHeader _header;
    /** 事件总数(含还在缓冲里的)。 */
    private long _cursor;
    /** 已经发出去的号的上界: 发号比落盘早, 不能拿游标当"下一个号"。 */
    private long _issued;
    /** 确认已落盘的条数: 重编号的起点只能在这里之后。 */
    private long _durableCursor;
    private bool _materialized;

    protected BufferedSessionHandle(SessionId id, SessionHeader header, SessionAccess access,
        long cursor, bool materialized, long inheritedEventCount)
    {
        Id = id;
        _header = header;
        Access = access;
        _cursor = cursor;
        _durableCursor = cursor;
        _issued = cursor;
        _materialized = materialized;
        InheritedEventCount = inheritedEventCount;
    }

    public SessionId Id { get; }
    public SessionHeader Header => _header;
    public SessionAccess Access { get; }
    public long InheritedEventCount { get; }
    public Func<long, IReadOnlyList<SessionEvent>>? TailSource { get; set; }
    public Func<long, IReadOnlyList<SessionEvent>>? RebaseSource { get; set; }
    public ISessionSequencer? Sequencer => Access == SessionAccess.Write ? this : null;
    public long DurableCount => _durableCursor;

    /** 缓冲里攒到这么多条就先落盘(字节预算由各后端的帧切分自己管)。 */
    protected abstract int MaxBufferedEvents { get; }

    /** 缓冲里的事件等这么久就落盘(崩溃窗口的上界)。 */
    protected abstract TimeSpan DrainInterval { get; }

    /** 库里/磁盘上已落盘的条数。 */
    protected abstract long StoredCount();

    /** 按序号区间读已落盘的事件。 */
    protected abstract IReadOnlyList<SessionEvent> ReadStored(long offset, long? length);

    /** 把这一批事件落到本后端的存储里; 返回后必须已经持久。 */
    protected abstract void CommitEvents(IReadOnlyList<SessionEvent> events);

    /** 只有头、没有事件的那一次落盘(空会话改名/首次 Flush)。 */
    protected abstract void CommitHeaderOnly();

    /** 写不进去时把这一段另存一份(事件是用户刚说的话)。 */
    protected abstract void SaveTail(IReadOnlyList<SessionEvent> events, Exception error);

    /** 落盘前的准备(文件后端要修尾、对账、截断半帧)。 */
    protected virtual void PrepareCommit()
    {
    }

    /** 落盘成功之后(文件后端要推进写票)。 */
    protected virtual void AfterCommit()
    {
    }

    /** 缓冲/游标变化后的钩子(文件后端据此丢缓存、记长度)。 */
    protected virtual void OnBufferedChanged()
    {
    }

    /** 按真实条数重新对齐之后的钩子(文件后端要把"已知长度"作废, 否则追加路径会拿旧长度对账)。 */
    protected virtual void OnAlignedToStorage()
    {
    }

    /** 按真实条数重编号之后的钩子。 */
    protected virtual void OnRebased()
    {
    }

    /** 缓冲里的起点序号(基类按"游标 - 缓冲条数"算)。 */
    protected long BufferedFrom => _cursor - _buffered.Count;

    /** 这一批的起点之后的游标(含缓冲)。 */
    protected long Cursor => _cursor;

    /** 存储里已经有这份日志(头写过盘)。 */
    protected bool Materialized => _materialized;

    public long Count
    {
        get
        {
            lock (_gate)
            {
                AssertOpen("count");
                if (PrimedCount is { } primed)
                    return primed;
                if (Access == SessionAccess.Write && _materialized)
                    return _cursor;
            }
            return Read().Count;
        }
    }

    /** 文件后端在窗口化开写时预先读进内存的事件数; 其余实现返回 null。 */
    protected virtual long? PrimedCount => null;

    /** 预先读进内存的那一段(窗口化开写的缓存)。 */
    protected virtual bool TryReadPrimed(long offset, long? length, out IReadOnlyList<SessionEvent> events)
    {
        events = [];
        return false;
    }

    internal void UpdateHeader(SessionHeader header)
    {
        lock (_gate)
        {
            AssertOpen("rename");
            _header = header;
        }
    }

    public IReadOnlyList<SessionEvent> Read(long offset = 0, long? length = null)
    {
        lock (_gate)
        {
            AssertOpen("read");
            if (offset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), "read offset must be a non-negative integer");
            if (length is < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "read length must be a non-negative integer");
            DrainLocked();
            return TryReadPrimed(offset, length, out var primed) ? primed : ReadStored(offset, length);
        }
    }

    public void Append(IReadOnlyList<SessionEvent> events)
    {
        lock (_gate)
        {
            AssertOpen("append");
            if (Access != SessionAccess.Write)
                throw new SessionReadOnlyException(Id, "append");
            if (events.Count == 0)
                return;
            AlignToBatch(events);
            var wasEmpty = _buffered.Count == 0;
            foreach (var sessionEvent in events)
            {
                if (_buffered.Count >= MaxBufferedEvents)
                    DrainLocked();
                _buffered.Add(sessionEvent);
                _cursor += 1;
            }
            _issued = Math.Max(_issued, _cursor);
            OnBufferedChanged();
            // 首次落盘、上次卡住、缓冲吃满: 立刻写。其余按时间窗攒批(消息与 chunk 一视同仁)。
            if (!_materialized || _stalled || _buffered.Count >= MaxBufferedEvents)
                DrainLocked();
            else if (wasEmpty)
                ScheduleDrain();
        }
    }

    /**
     * 一批事件的起点必须接在游标上; 只接受两种"跟上去":
     * 别人的事件真的已经写进存储(向上), 或会话按真实条数重编号(向下)。其余一律拒绝 —— 那说明有人在猜序号。
     */
    private void AlignToBatch(IReadOnlyList<SessionEvent> events)
    {
        if (events[0].Seq != _cursor)
        {
            var stored = StoredCount();
            if (events[0].Seq > _cursor && _buffered.Count == 0 && stored == events[0].Seq)
            {
                _cursor = events[0].Seq;
                _issued = Math.Max(_issued, _cursor);
            }
            else if (events[0].Seq < _cursor && events[0].Seq >= stored)
            {
                _buffered.Clear();
                _cursor = events[0].Seq;
                _issued = events[0].Seq;
            }
            else
            {
                throw Mismatch(_cursor, 0, events[0].Seq, stored);
            }
        }
        for (var index = 0; index < events.Count; index += 1)
        {
            if (events[index].Seq != _cursor + index)
                throw Mismatch(_cursor + index, index, events[index].Seq, StoredCount());
        }
    }

    private SessionPersistenceCorruptionException Mismatch(long expected, int index, long got, long stored)
        => new($"append seq mismatch for \"{Id}\": expected {expected} at index {index}, got {got} (log holds {stored})");

    public void Flush()
    {
        lock (_gate)
        {
            AssertOpen("flush");
            if (Access != SessionAccess.Write)
                throw new SessionReadOnlyException(Id, "flush");
            DrainLocked();
            if (_materialized)
                return;
            CommitHeaderOnly();
            _materialized = true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            _timer?.Dispose();
            try
            {
                DrainLocked();
            }
            catch (SessionPersistenceException error)
            {
                // 这批事件不能丢: 先另存一份, 再决定要不要把失败交出去。
                SaveTail(_buffered, error);
                _buffered.Clear();
                // 写权不在自己手里是"停写"的正常结局; 存储被改坏之类必须让调用方知道。
                if (error is not SessionWriteBusyException)
                    throw;
            }
            finally
            {
                ReleaseResources();
            }
        }
    }

    public void Dispose() => Close();

    /** 领下一个号: 事件在创建之前就定下号, 广播因此可以早于落盘。 */
    public long NextSeq()
    {
        if (Access != SessionAccess.Write)
            throw new SessionReadOnlyException(Id, "number events");
        lock (_gate)
        {
            AssertOpen("number events");
            return NextSeqLocked();
        }
    }

    /** 发号实现: 默认按内存计数器; 需要写票的后端覆盖它。 */
    protected virtual long NextSeqLocked()
    {
        _issued = Math.Max(_issued, _cursor);
        return _issued++;
    }

    /** 读回 [from, to) 的事件(把内存对齐到存储): 默认直读, 文件后端覆盖成"不做日志缩水判定"的那条路。 */
    public virtual IReadOnlyList<SessionEvent> ReadRange(long from, long to)
        => ReadStored(from, Math.Max(0, to - from));

    /** 释放后端资源(句柄登记、写票): 关闭时总会被调到一次。 */
    protected virtual void ReleaseResources()
    {
    }

    /** 把缓冲写盘; 失败时缓冲留着, 下次落盘重试整批。 */
    protected void DrainLocked()
    {
        if (_buffered.Count == 0)
            return;
        try
        {
            DrainCoreLocked();
        }
        catch (SessionNumberStaleException stale) when (RebaseSource is not null && TryRebaseLocked(stale.StartSeq))
        {
            // 这批的号已经被别人接管: 会话把它们整体平移, 我们按新号照常写, 事件一条不丢。
            DrainCoreLocked();
        }
        catch (SessionPersistenceCorruptionException) when (TryAlignToDiskLocked())
        {
            // 存储比句柄记得的少(上一批没落成/被回退过): 按真实条数整段补一遍再写。
            DrainCoreLocked();
        }
    }

    private void DrainCoreLocked()
    {
        try
        {
            PrepareCommit();
            CommitEvents([.. _buffered]);
            _materialized = true;
            _buffered.Clear();
            _durableCursor = _cursor;
            _stalled = false;
            AfterCommit();
        }
        catch (SessionPersistenceException)
        {
            // 记住"卡住了": 之后每条事件都先重试一次落盘, 对方一退出就自动补上。
            _stalled = true;
            throw;
        }
    }

    /** 存储落后于内存: 游标退到真实条数, 缓冲换成"从那里开始的全部事件"。 */
    protected bool TryAlignToDiskLocked()
    {
        if (TailSource is not { } source)
            return false;
        var stored = StoredCount();
        if (stored >= _cursor)
            return false;
        var missing = source(stored);
        if (missing.Count == 0)
            return false;
        _buffered.Clear();
        _buffered.AddRange(missing);
        _cursor = stored + missing.Count;
        _issued = _cursor;
        _durableCursor = stored;
        OnBufferedChanged();
        OnAlignedToStorage();
        return true;
    }

    /** 号过期: 让会话按真实条数重编号, 换上新的缓冲与游标。 */
    protected bool TryRebaseLocked(long startSeq)
    {
        if (RebaseSource is not { } source)
            return false;
        IReadOnlyList<SessionEvent> rebased;
        try
        {
            rebased = source(Math.Max(0, startSeq));
        }
        catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException)
        {
            throw new SessionWriteBusyException(Environment.ProcessId,
                $"the buffered events could not be renumbered onto the log ({error.GetType().Name}); retrying on the next write");
        }
        if (rebased.Count == 0)
            return false;
        _buffered.Clear();
        _buffered.AddRange(rebased);
        _cursor = rebased[0].Seq + rebased.Count;
        _issued = _cursor;
        _durableCursor = rebased[0].Seq;
        OnBufferedChanged();
        OnRebased();
        return true;
    }

    private void ScheduleDrain()
    {
        _timer ??= new Timer(_ => DrainFromTimer());
        _timer.Change(DrainInterval, Timeout.InfiniteTimeSpan);
    }

    private void DrainFromTimer()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            try
            {
                DrainLocked();
            }
            catch (SessionPersistenceException)
            {
                // 定时器线程上抛出会终止进程; 缓冲仍在, 下一次同步落盘会重试并抛给调用方。
                if (_buffered.Count > 0 && !_closed)
                    ScheduleDrain();
            }
        }
    }

    private void AssertOpen(string operation)
    {
        if (_closed)
            throw new SessionHandleClosedException(Id, operation);
    }
}


