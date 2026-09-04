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
}

internal sealed class JsonlSessionHandle(
    JsonlSessionPersistence storage,
    SessionId id,
    SessionHeader header,
    SessionAccess access,
    StorageHandleState state) : ISessionHandle
{
    private readonly object _gate = new();
    private bool _closed;
    private long _observedLength;

    public SessionId Id { get; } = id;
    public SessionHeader Header { get; } = header;
    public SessionAccess Access { get; } = access;
    public long InheritedEventCount => state.InheritedEventCount;

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
            if (state.TornTruncateTo is { } truncateTo)
            {
                storage.TruncateTornTail(Header, truncateTo);
                state.TornTruncateTo = null;
            }
            if (state.RecoveredTail is { } recovered)
            {
                if (recovered.Count > 0)
                    storage.PersistBatch(Header, recovered, state.Materialized, state.InheritedEventCount);
                state.RecoveredTail = null;
            }
            storage.PersistBatch(Header, events, state.Materialized, state.InheritedEventCount);
            state.Materialized = true;
            state.Cursor += events.Count;
            state.Primed = null;
            _observedLength = state.Cursor;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            AssertOpen("flush");
            if (Access != SessionAccess.Write) throw new SessionReadOnlyException(Id, "flush");
            if (state.Materialized) return;
            storage.PersistHeader(Header, state.InheritedEventCount);
            state.Materialized = true;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
            storage.ReleaseHandle(this, state.Materialized);
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
