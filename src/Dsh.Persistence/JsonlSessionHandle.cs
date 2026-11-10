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

    /** 本写入者上一次离开日志时的字节长度; null 表示需要重新对齐(还没落盘或刚被外部重写过)。 */
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
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset), "read offset must be a non-negative integer");
            var limit = length ?? long.MaxValue;
            if (limit < 0) throw new ArgumentOutOfRangeException(nameof(length), "read length must be a non-negative integer");
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
            for (var index = 0; index < events.Count; index += 1)
            {
                if (events[index].Seq != state.Cursor + index)
                    throw new SessionPersistenceCorruptionException(
                        $"append seq mismatch for \"{Id}\": expected {state.Cursor + index} at index {index}, got {events[index].Seq}");
            }
            var wasEmpty = _buffered.Count == 0;
            _buffered.AddRange(events);
            state.Cursor += events.Count;
            state.Primed = null;
            _observedLength = state.Cursor;
            if (_buffered.Count >= storage.WriteBatch.MaxEvents || events.Any(sessionEvent => sessionEvent.Data is not AssistantChunkPayload))
                DrainLocked();
            else if (wasEmpty)
                ScheduleDrain();
        }
    }

    /** 先把缓冲写盘再在句柄锁内执行 action, 供需要独占日志文件的操作(重写头部)使用。 */
    internal void WhileDrained(Action action)
    {
        lock (_gate)
        {
            if (!_closed)
                DrainLocked();
            action();
            state.WrittenLength = null;
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
            if (eventCount < 0) throw new ArgumentOutOfRangeException(nameof(eventCount), "truncate target must be non-negative");
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
