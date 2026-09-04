using Dsh.Llm;

namespace Dsh.Core;

public sealed class Session
{
    private readonly List<SessionEvent> _log = [];
    private readonly Surface.Manager _surfaceManager;
    private SessionEvent[]? _eventsSnapshot;
    private EpochHeader? _headerFold;
    private long _headerFoldSeq;
    private RequestContextPayload? _contextFold;
    private long _contextFoldSeq;
    private List<Message> _derived = [];
    private int _derivedNodes;
    private int _derivedGeneration;

    public event Action<Session, SessionEvent>? Appended;

    public SessionHeader Header { get; }

    public SessionId Id => Header.Id;

    public long InheritedEventCount { get; }

    public long FirstLiveSeq { get; }

    public Surface.Manager SurfaceManager => _surfaceManager;

    private Session(SessionId id, IReadOnlyList<SessionEvent>? seed, SessionHeader? header, bool restore, long? suppliedInheritedEventCount)
    {
        _surfaceManager = new Surface.Manager(_log);
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
                _log.Add(seedEvent);
            }
        }
        FirstLiveSeq = _log.Count;
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
        if (inheritedEventCount > _log.Count)
            throw new ArgumentException("session inherited event count exceeds its event log");
        InheritedEventCount = inheritedEventCount;
        if (seed is not null && (_log.Count == 0 || _log[^1].Type != SessionEventTypes.SessionEndSeed))
            Append(new SessionEndSeedPayload());
    }

    public static Session Create(SessionId id, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
        => new(id, seed, header, false, inheritedEventCount);

    public static Session FromRestore(SessionId id, IReadOnlyList<SessionEvent> seed, SessionHeader header, long inheritedEventCount)
        => new(id, seed, header, true, inheritedEventCount);

    public SessionEvent? EventAt(long seq) => seq >= 0 && seq < _log.Count ? _log[(int)seq] : null;

    public IReadOnlyList<SessionEvent> SnapshotEvents(long fromSeq = 0, long? toSeqExclusive = null)
    {
        var to = toSeqExclusive ?? Seq;
        if (fromSeq == 0 && to == _log.Count)
            return _eventsSnapshot ??= [.._log];
        return _log.GetRange((int)fromSeq, (int)(to - fromSeq));
    }

    public IReadOnlyList<SessionEvent> OwnEvents() => SnapshotEvents(InheritedEventCount);

    public bool IsOwnSeq(long seq) => seq >= InheritedEventCount && seq < Seq;

    public long Seq => _log.Count;

    public SessionEvent Append(SessionEventPayload payload, SurfaceOp? surfaceOp = null, IReadOnlyList<long>? sourceEventSeqs = null)
    {
        var sessionEvent = new SessionEvent
        {
            Type = payload.Type,
            Seq = _log.Count,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Data = payload,
            SurfaceOp = surfaceOp,
            SourceEventSeqs = sourceEventSeqs,
        };
        _surfaceManager.ValidateNext(sessionEvent);
        var subscribers = Appended;
        _log.Add(sessionEvent);
        _eventsSnapshot = null;
        if (subscribers is not null)
        {
            foreach (Action<Session, SessionEvent> subscriber in subscribers.GetInvocationList())
            {
                try
                {
                    subscriber(this, sessionEvent);
                }
                catch
                {
                    // Observer failures are contained and never unmake a committed append.
                }
            }
        }
        return sessionEvent;
    }

    public EpochHeader? RequestHeader()
    {
        if (_headerFoldSeq < _log.Count)
        {
            _headerFold = Core.RequestHeader.Fold(_log.GetRange((int)_headerFoldSeq, (int)(_log.Count - _headerFoldSeq)), _headerFold);
            _headerFoldSeq = _log.Count;
        }
        return _headerFold;
    }

    public RequestContextPayload? RequestContext()
    {
        if (_contextFoldSeq < _log.Count)
        {
            foreach (var sessionEvent in _log.GetRange((int)_contextFoldSeq, (int)(_log.Count - _contextFoldSeq)))
            {
                if (sessionEvent.Data is RequestContextPayload context)
                    _contextFold = context;
            }
            _contextFoldSeq = _log.Count;
        }
        return _contextFold;
    }

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
        for (var index = _derivedNodes; index < nodes.Count; index++)
        {
            var message = Surface.DeriveEventMessage(_log[(int)nodes[index]]);
            if (message is not null)
                _derived.Add(message);
        }
        _derivedNodes = nodes.Count;
        return [.._derived];
    }
}
