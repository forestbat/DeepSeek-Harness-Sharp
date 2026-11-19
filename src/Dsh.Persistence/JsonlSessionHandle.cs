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
    StorageHandleState state) : ISessionHandle
{
    private readonly object _gate = new();
    private readonly List<SessionEvent> _buffered = [];
    private Timer? _drainTimer;
    private bool _closed;
    private long _observedLength;
    private SessionHeader _header = header;

    public SessionId Id { get; } = id;
    public SessionHeader Header => _header;
    public SessionAccess Access { get; } = access;
    public long InheritedEventCount => state.InheritedEventCount;

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
            if (!state.Materialized || _buffered.Count >= storage.WriteBatch.MaxEvents)
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
            }
        }
    }

    public void Truncate(long eventCount)
    {
        lock (_gate)
        {
            AssertOpen("truncate");
            if (Access != SessionAccess.Write) throw new SessionReadOnlyException(Id, "truncate");
            if (eventCount < 0)
                throw new ArgumentOutOfRangeException(nameof(eventCount), "truncate target must be non-negative");
            DrainLocked();
            if (state.Materialized && eventCount >= state.Cursor)
                return;
            if (state.Materialized)
                state.WrittenLength = storage.TruncateLog(Header, eventCount) ?? state.WrittenLength;
            state.Cursor = eventCount;
            state.Materialized = state.Materialized || eventCount == 0;
            state.Primed = null;
            state.TornTruncateTo = null;
            state.RecoveredTail = null;
            _observedLength = eventCount;
        }
    }

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
                DrainLocked();
            }
            catch (SessionLeaseLostException)
            {
                // 已被人接管: 缓冲区里的事件不能再落盘, 丢弃并让出写权 —— 这就是"停写"。
                _buffered.Clear();
            }
            finally
            {
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
