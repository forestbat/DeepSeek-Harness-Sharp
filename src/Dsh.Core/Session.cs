using System.Collections;
using Dsh.Llm;

namespace Dsh.Core;

public sealed class Session
{
    private SessionEvent[] _buffer = [];
    private int _count;
    private SessionLog _snapshot = SessionLog.Empty;
    private readonly Lock _writeGate = new();
    private Surface.Manager _surfaceManager;
    private EpochHeader? _headerFold;
    private RequestContextPayload? _contextFold;
    private List<Message> _derived = [];
    private int _derivedNodes;
    private int _derivedGeneration;

    public event Action<Session, SessionEvent>? Appended;

    public event Action<Session, long>? Truncated;

    public event Action<Session, SessionHeader>? Renamed;

    public SessionHeader Header { get; private set; }

    public SessionId Id => Header.Id;

    public long InheritedEventCount { get; }

    public long FirstLiveSeq { get; }

    public Surface.Manager SurfaceManager => _surfaceManager;

    private Session(SessionId id, IReadOnlyList<SessionEvent>? seed, SessionHeader? header, long? suppliedInheritedEventCount)
    {
        _surfaceManager = new Surface.Manager(() => Volatile.Read(ref _snapshot));
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
        FirstLiveSeq = _count;
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
        if (Header.IsSeeded && seed is null)
            throw new ArgumentException("seeded session requires an explicit constructor seed");
        if (Header.IsSeeded && suppliedInheritedEventCount is null)
            throw new ArgumentException("seeded session requires an inherited event count");
        var inheritedEventCount = suppliedInheritedEventCount ?? 0;
        if (!Header.IsSeeded && inheritedEventCount != 0)
            throw new ArgumentException("unseeded session inherited event count must be 0");
        if (inheritedEventCount > _count)
            throw new ArgumentException("session inherited event count exceeds its event log");
        InheritedEventCount = inheritedEventCount;
        if (seed is not null && (_count == 0 || _buffer[_count - 1].Type != SessionEventTypes.SessionEndSeed))
            Append(new SessionEndSeedPayload());
    }

    public static Session Create(SessionId id, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
        => new(id, seed, header, inheritedEventCount);

    public static Session FromRestore(SessionId id, IReadOnlyList<SessionEvent> seed, SessionHeader header, long inheritedEventCount)
        => new(id, seed, header, inheritedEventCount);

    public SessionEvent? EventAt(long seq)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return seq >= 0 && seq < snapshot.Count ? snapshot[(int)seq] : null;
    }

    public IReadOnlyList<SessionEvent> SnapshotEvents(long fromSeq = 0, long? toSeqExclusive = null)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var to = toSeqExclusive ?? snapshot.Count;
        if (fromSeq == 0 && to == snapshot.Count)
            return snapshot;
        if (fromSeq < 0 || to > snapshot.Count || fromSeq > to)
            throw new ArgumentOutOfRangeException(
                nameof(fromSeq),
                $"invalid snapshot range [{fromSeq}, {to}) for {snapshot.Count} events");
        return snapshot.Slice((int)fromSeq, (int)to);
    }

    public IReadOnlyList<SessionEvent> OwnEvents() => SnapshotEvents(InheritedEventCount);

    public bool IsOwnSeq(long seq) => seq >= InheritedEventCount && seq < Seq;

    public long Seq => Volatile.Read(ref _snapshot).Count;

    /** 就地截断到 eventCount: 只剩 [0, eventCount), 重建 surface 与 header/context 折叠。仅限本会话自有区间。 */
    public bool TryTruncateTo(long eventCount)
    {
        long truncatedTo;
        lock (_writeGate)
        {
            if (eventCount < InheritedEventCount || eventCount > _count)
                return false;
            if (eventCount == _count)
                return true;
            _count = (int)eventCount;
            Volatile.Write(ref _snapshot, new SessionLog(_buffer, 0, _count));
            // surface 与折叠状态由事件日志派生: 日志缩短后整体重建, 不能就地回退(见 D3 revert)。
            _surfaceManager = new Surface.Manager(() => Volatile.Read(ref _snapshot));
            _headerFold = null;
            _contextFold = null;
            for (var index = 0; index < _count; index++)
            {
                switch (_buffer[index].Data)
                {
                    case RequestHeaderPayload header:
                        _headerFold = Core.RequestHeader.Canonicalize(header.Header);
                        break;
                    case RequestContextPayload context:
                        _contextFold = context;
                        break;
                }
            }

            _derived = [];
            _derivedNodes = 0;
            _derivedGeneration = -1;
            truncatedTo = _count;
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
                Seq = _count,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Data = payload,
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
            var message = Surface.DeriveEventMessage(snapshot[(int)nodes[index]]);
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
        switch (sessionEvent.Data)
        {
            case RequestHeaderPayload header:
                Volatile.Write(ref _headerFold, Core.RequestHeader.Canonicalize(header.Header));
                break;
            case RequestContextPayload context:
                Volatile.Write(ref _contextFold, context);
                break;
        }
        Volatile.Write(ref _snapshot, new SessionLog(_buffer, 0, _count));
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

    // 单写者追加、发布不可变 (数组, 长度) 视图: 读者取一次引用后无锁遍历, 元素先写、长度后发布, 故不会读到未写入的槽。
    private sealed class SessionLog(SessionEvent[] items, int offset, int count) : IReadOnlyList<SessionEvent>
    {
        public static readonly SessionLog Empty = new([], 0, 0);

        public int Count => count;

        public SessionEvent this[int index]
            => index >= 0 && index < count
                ? items[offset + index]
                : throw new ArgumentOutOfRangeException(nameof(index));

        public SessionLog Slice(int from, int to) => new(items, offset + from, to - from);

        public IEnumerator<SessionEvent> GetEnumerator()
        {
            for (var i = 0; i < count; i++)
                yield return items[offset + i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
