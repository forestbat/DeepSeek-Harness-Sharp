using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

internal sealed class StorageHandleState
{
    public required long Cursor { get; set; }
    public required bool Materialized { get; set; }
    public long? TornTruncateTo { get; set; }
    public List<SessionEvent>? RecoveredTail { get; set; }
    public required long InheritedEventCount { get; init; }
    public List<SessionEvent>? Primed { get; set; }

    /** Bytes written by this handle; null until the log is materialized. */
    public long? WrittenLength { get; set; }
}

internal sealed class JsonlSessionHandle(
    JsonlSessionPersistence storage,
    SessionId id,
    SessionHeader header,
    SessionAccess access,
    StorageHandleState state) : ISessionHandle, ISessionSequencer
{
    private readonly object _gate = new();
    private readonly List<SessionEvent> _buffered = [];
    private Timer? _drainTimer;
    private bool _closed;
    /** 上一次落盘没写成功(通常是写租约暂时在别人手里): 之后每条事件都先重试一次。 */
    private bool _stalled;
    private long _observedLength;
    /** 成功落盘到的条数: 重编号的起点只能在这里之后。 */
    private long _durableCursor;
    private SessionHeader _header = header;

    public SessionId Id { get; } = id;
    public SessionHeader Header => _header;
    public SessionAccess Access { get; } = access;
    public long InheritedEventCount => state.InheritedEventCount;
    public Func<long, IReadOnlyList<SessionEvent>>? TailSource { get; set; }

    public Func<long, IReadOnlyList<SessionEvent>>? RebaseSource { get; set; }

    /** 发号凭证: 起点一律以日志真实条数为准, 票由 JSONL 后端记在会话目录里。 */
    public ISessionSequencer? Sequencer => this;

    public long DurableCount => _durableCursor;

    /** 写句柄的事件总数就是游标; 读句柄没有别的地方知道总数, 只能退回整份读(已有缓存)。 */
    public long Count
    {
        get
        {
            lock (_gate)
            {
                AssertOpen("count");
                if (state.Primed is { } primed)
                    return primed.Count;
                if (Access == SessionAccess.Write && state.Materialized)
                    return state.Cursor;
            }

            return Read().Count;
        }
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
            var limit = length ?? long.MaxValue;
            if (limit < 0)
                throw new ArgumentOutOfRangeException(nameof(length), "read length must be a non-negative integer");
            if (state.Primed is { } primed)
            {
                _observedLength = Math.Max(_observedLength, primed.Count);
                return Slice(primed, offset, limit);
            }

            DrainLocked();
            if (Access == SessionAccess.Write && !state.Materialized) return [];
            var path = storage.ResolveLog(Id);
            if (path is null)
            {
                if (storage.HasPendingSession(Id)) return [];
                throw new SessionPersistenceNotFoundException(Id);
            }

            // 要一个窗口就走缺页重读: 解压命中帧即可, 不必把整份日志展开成事件数组。
            if (length is { } window && window < int.MaxValue && offset > 0
                && storage.TryReadWindow(path, Id, offset, (int)window) is { } windowed)
            {
                // 窗口已按 [offset, offset + window) 过滤过, 这里不能再按绝对 offset 切一遍。
                // 也不更新 _observedLength: 窗口不代表日志总量, 那会让后续全量读误判"日志缩水"。
                return windowed.Events;
            }

            var events = storage.ReadStoredLog(path, Id).Events;
            if (events.Count < _observedLength)
                throw new SessionPersistenceCorruptionException(
                    $"session \"{Id}\": stored log shrank below a previously observed prefix ({events.Count} < {_observedLength})");
            _observedLength = events.Count;
            return Slice(events, offset, limit);
        }
    }

    public void Append(IReadOnlyList<SessionEvent> events)
    {
        lock (_gate)
        {
            AssertOpen("append");
            if (Access != SessionAccess.Write) throw new SessionReadOnlyException(Id, "append");
            if (events.Count == 0) return;
            var wasEmpty = _buffered.Count == 0;
            // 序号以会话发给我们的这一批为准: 会话在发号票下已经对齐过日志真实条数。
            // 只有两种"跟上去"是合法的: 别人的事件真的已经写进日志(向上), 或会话按日志重编号(向下)。
            // 其余一律照旧拒绝 —— 那说明有人在猜序号, 而猜错的代价是整条日志读不回来。
            if (events[0].Seq != state.Cursor)
            {
                var stored = storage.StoredEventCount(Id);
                if (events[0].Seq > state.Cursor && _buffered.Count == 0 && stored == events[0].Seq)
                    state.Cursor = events[0].Seq;
                else if (events[0].Seq < state.Cursor && events[0].Seq >= stored)
                {
                    _buffered.Clear();
                    state.Cursor = events[0].Seq;
                }
                else
                {
                    throw new SessionPersistenceCorruptionException(
                        $"append seq mismatch for \"{Id}\": expected {state.Cursor}, got {events[0].Seq} (log holds {stored})");
                }
            }
            // 先整体校验序号: 一批里有一条对不上就整批拒绝, 不能把前一半悄悄收进缓冲。
            for (var index = 0; index < events.Count; index += 1)
            {
                if (events[index].Seq != state.Cursor + index)
                    throw new SessionPersistenceCorruptionException(
                        $"append seq mismatch for \"{Id}\": expected {state.Cursor + index} at index {index}, got {events[index].Seq}");
            }

            foreach (var sessionEvent in events)
            {
                // 缓冲吃满条数上限就先落盘: 条数是唯一能在"收进缓冲之前"结算的判据。
                // 字节预算不在这里判 —— 一行占多少字节只有编码之后才知道(见 DrainLocked)。
                if (_buffered.Count >= storage.WriteBatch.MaxEvents)
                    DrainLocked();
                _buffered.Add(sessionEvent);
                state.Cursor += 1;
            }

            state.Primed = null;
            _observedLength = state.Cursor;
            // 首次落盘立刻写: 日志文件的存在与可见性(会话目录、侧栏列表)依赖它。
            // 之后的每一批(消息、工具调用、回合边界与 chunk 一视同仁)按"条数上限 / Interval"攒批,
            // 边界事件不再单独触发落盘 —— 那是帧粒度退化成"一条事件一帧"的原因。
            if (!state.Materialized || _stalled || _buffered.Count >= storage.WriteBatch.MaxEvents)
                DrainLocked();
            else if (wasEmpty)
                ScheduleDrain();
        }
    }

    /** Drains buffered events and records the length returned by an exclusive rewrite. */
    internal void WhileDrained(Func<long?, long> action)
    {
        lock (_gate)
        {
            if (!_closed)
                DrainLocked();
            var rewrittenLength = action(state.WrittenLength);
            if (state.TornTruncateTo is { } truncateTo && state.WrittenLength is { } previousLength)
                state.TornTruncateTo = truncateTo + rewrittenLength - previousLength;
            state.WrittenLength = rewrittenLength;
        }
    }

    /** 缓冲的事件写成一个批次; 写盘失败时保留缓冲, 下次落盘重试整批。 */
    private void DrainLocked()
    {
        if (_buffered.Count == 0)
            return;
        try
        {
            DrainCoreLocked();
        }
        catch (SessionPersistenceCorruptionException) when (TryAlignToDiskLocked())
        {
            // 磁盘比句柄记得的少(上一批没写成功、日志被回退过): 按磁盘真实条数整段补一遍再写。
            // 用户的事件一条都不丢, 也不因为一次落后就永久停写。
            DrainCoreLocked();
        }
        catch (SessionNumberStaleException stale) when (RebaseSource is not null && TryRebaseLocked(stale.StartSeq))
        {
            // 这批的号已经被别人接管: 会话把它们整体平移, 我们按新号照常写, 事件一条不丢。
            DrainCoreLocked();
        }
    }

    private void DrainCoreLocked()
    {
        try
        {
            // 尾巴上有无法归属的字节: 现在就修掉。宿主可能连续开着几天, "等下次打开再修"等于这几天一条都不写。
            if (state.Materialized && !storage.IsLogTailClean(Id))
            {
                long repaired;
                try
                {
                    repaired = storage.RepairDamagedTail(Id);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException)
                {
                    // 修不动不代表事件可以丢: 按"这次先别写"处理, 缓冲留着, 下一次落盘再试。
                    throw new SessionWriteBusyException(Environment.ProcessId,
                        $"the log tail could not be repaired right now ({error.GetType().Name}); retrying on the next write");
                }
                if (repaired != state.Cursor && !TryRebaseLocked(repaired))
                    TryAlignToDiskLocked();
                state.WrittenLength = null;
            }
            // 日志在上一批之后变过: 在拿尾锁**之前**先把内存对齐好 —— 持锁时读整份日志会撞上自己占住的那段字节。
            // 盘上少(我们的某一批没落成)就整段补写; 盘上多(别的写者推进了)就按真实条数重编号。
            if (state.Materialized && state.WrittenLength is { } knownLength
                && storage.LogLength(Id) is { } currentLength && currentLength != knownLength)
            {
                var stored = storage.StoredEventCount(Id);
                // 盘上已经越过了"我上次落盘的位置": 说明有人占用了我们发出去的号, 只能重编号。
                if (stored > _durableCursor)
                {
                    if (storage.IsLogTailClean(Id))
                        TryRebaseLocked(stored);
                }
                else if (stored < state.Cursor)
                {
                    // 我们自己的某一批没落成: 整段补写。尾巴脏时不动文件, 交给追加路径报损伤。
                    TryAlignToDiskLocked();
                }
            }
            if (state.TornTruncateTo is { } truncateTo)
            {
                storage.TruncateTornTail(Header, truncateTo);
                state.TornTruncateTo = null;
                state.WrittenLength = truncateTo;
            }

            if (state.RecoveredTail is { } recovered)
            {
                if (recovered.Count > 0)
                    storage.PersistBatch(Header, recovered, state);
                state.Materialized = true;
                state.RecoveredTail = null;
            }

            storage.PersistBatch(Header, _buffered, state);
            state.Materialized = true;
            _buffered.Clear();
            _stalled = false;
            _durableCursor = state.Cursor;
            // 票随落盘前进: 没有未落盘事件时它会把自己删掉, 空闲进程不占发号权。
            storage.MarkDurable(Header, state.Cursor);
        }
        catch (SessionPersistenceException)
        {
            // 记住"卡住了": 之后每条事件都先重试一次落盘, 对方一退出就自动补上, 不必等攒够一批。
            _stalled = true;
            throw;
        }
    }

    /**
     * 落盘与内存对不上时, 以磁盘为准重新对齐: 游标退到磁盘真实条数, 缓冲换成"从那里开始的全部事件"。
     *
     * 只处理"磁盘落后于内存"(我们的某批没写成功); 磁盘超前说明有别的写者写进了我们没有的事件,
     * 那不是本层能补的, 照旧抛给上层。
     */
    private bool TryAlignToDiskLocked()
    {
        if (TailSource is not { } source)
            return false;
        // 末尾有不属于任何事件的字节时不能往上写: 追加会落在它们后面, 之后整条日志都读不回来。
        if (!storage.IsLogTailClean(Id))
            return false;
        var stored = storage.StoredEventCount(Id);
        if (stored >= state.Cursor)
            return false;
        var missing = source(stored);
        if (missing.Count == 0)
            return false;
        _buffered.Clear();
        _buffered.AddRange(missing);
        state.Cursor = stored;
        state.Primed = null;
        state.Materialized = true;
        // 长度以文件实际为准: 日志被回退过时, 拿旧长度当判据会立刻再判一次不一致。
        state.WrittenLength = null;
        _observedLength = stored;
        _stalled = false;
        return true;
    }

    /** 号过期: 让会话按日志真实条数重编号, 换上新的缓冲与游标。 */
    private bool TryRebaseLocked(long startSeq)
    {
        if (RebaseSource is not { } source)
            return false;
        IReadOnlyList<SessionEvent> rebased;
        try
        {
            rebased = source(startSeq);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException)
        {
            // 重编号读不动日志(结构异常/解析失败): 事件还在缓冲里, 按"这次先别写"处理, 别把裸异常扔进回合。
            throw new SessionWriteBusyException(Environment.ProcessId,
                $"the buffered events could not be renumbered onto the log ({error.GetType().Name}); retrying on the next write");
        }
        _buffered.Clear();
        _buffered.AddRange(rebased);
        state.Cursor = startSeq;
        state.Primed = null;
        state.WrittenLength = null;
        _observedLength = startSeq;
        _stalled = false;
        return true;
    }

    private void ScheduleDrain()
    {
        _drainTimer ??= new Timer(_ => DrainFromTimer());
        _drainTimer.Change(storage.WriteBatch.Interval, Timeout.InfiniteTimeSpan);
    }

    /** 最早一条缓冲的 chunk 等满 Interval 后落盘。 */
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
            catch (Exception)
            {
                // 定时器线程上抛出会终止进程; 缓冲仍在, 下一次同步落盘会重试并抛给调用方。
                // 卡住时继续按间隔重试: 写权被收回来(对方退出)之后, 这一段事件就能自己补上。
                if (_stalled && _buffered.Count > 0)
                    ScheduleDrain();
            }
        }
    }

    /** 领票并返回下一个可用序号: 序号在事件创建之前就定下, 广播与落盘因此可以并行。 */
    public long NextSeq()
    {
        if (Access != SessionAccess.Write)
            throw new SessionReadOnlyException(Id, "number events");
        return storage.NextSeq(Header, 1, state.Cursor);
    }

    /** 读回日志上 [from, to) 的事件: 别的写者写进去的那一段。 */
    public IReadOnlyList<SessionEvent> ReadRange(long from, long to)
        => storage.ReadStoredRange(Id, from, to);


    public void Flush()
    {
        lock (_gate)
        {
            AssertOpen("flush");
            if (Access != SessionAccess.Write) throw new SessionReadOnlyException(Id, "flush");
            DrainLocked();
            if (state.Materialized) return;
            storage.PersistHeader(Header, state);
            state.Materialized = true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            _drainTimer?.Dispose();
            try
            {
                // 收尾这一次落盘会先把写权收回来(对方的进程已经退出时), 所以"停写过"的会话照样能补上。
                DrainLocked();
            }
            catch (SessionPersistenceException error)
            {
                // 这批事件不能丢: 先另存一份, 再决定要不要把失败交出去。
                storage.SaveUnsavedTail(Header, _buffered, error);
                _buffered.Clear();
                // 写权不在自己手里是"停写"的正常结局; 日志被别人改坏之类必须让调用方知道。
                if (error is not SessionWriteBusyException)
                    throw;
            }
            finally
            {
                if (Access == SessionAccess.Write)
                    storage.ReleaseTicket(Header);
                storage.ReleaseHandle(this, state.Materialized);
            }
        }
    }

    public void Dispose() => Close();

    private void AssertOpen(string operation)
    {
        if (_closed) throw new SessionHandleClosedException(Id, operation);
    }

    private static IReadOnlyList<SessionEvent> Slice(List<SessionEvent> events, long offset, long limit)
    {
        if (offset >= events.Count || limit == 0) return [];
        var count = (int)Math.Min(limit, events.Count - offset);
        return events.GetRange((int)offset, count);
    }
}
