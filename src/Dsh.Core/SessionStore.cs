using Cordis;
using Dsh.Llm;

namespace Dsh.Core;

public sealed class SessionStore(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "sessions";
    public const string CreatedEvent = "session/created";
    public const string DisposedEvent = "session/disposed";
    public const string EventEvent = "session/event";
    public const string FlushEvent = "session/flush";

    private sealed class Entry
    {
        public required Session Session { get; init; }
        public required Context Owner { get; init; }
        public bool Announced;
    }

    private readonly Dictionary<SessionId, Entry> _sessions = [];

    public Session Create(SessionId? id = null, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
    {
        var session = Session.Create(id ?? SessionId.Create(Guid.NewGuid().ToString()), seed, header, inheritedEventCount);
        Enter(session, Ctx);
        Announce(session);
        return session;
    }

    public Session Prepare(SessionId? id = null, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
        => Session.Create(id ?? SessionId.Create(Guid.NewGuid().ToString()), seed, header, inheritedEventCount);

    public IDisposable Enter(Session session, Context owner)
    {
        if (_sessions.ContainsKey(session.Id))
            throw new InvalidOperationException($"session \"{session.Id}\" is already live in the store");
        var entry = new Entry { Session = session, Owner = owner };
        _sessions[session.Id] = entry;
        Action<Session, SessionEvent> forward = (source, sessionEvent) => PublishEvent(source, sessionEvent);
        session.Appended += forward;
        return new SessionDetach(() =>
        {
            session.Appended -= forward;
            Detach(session);
        });
    }

    public void Announce(Session session)
    {
        if (!_sessions.TryGetValue(session.Id, out var entry))
            throw new InvalidOperationException($"session \"{session.Id}\" is not entered in the store");
        if (entry.Announced)
            return;
        entry.Announced = true;
        Ctx.Emit(CreatedEvent, session);
    }

    private void PublishEvent(Session session, SessionEvent sessionEvent)
    {
        Ctx.Events.Emit(Ctx, EventEvent, session, sessionEvent);
    }

    private void Detach(Session session)
    {
        if (!_sessions.Remove(session.Id))
            return;
        Ctx.Emit(DisposedEvent, session);
    }

    public async Task Flush(Session session)
        => await Ctx.Events.Parallel(Ctx, FlushEvent, session);

    public Session? Get(SessionId id)
        => _sessions.TryGetValue(id, out var entry) ? entry.Session : null;

    public IReadOnlyList<Session> List() => _sessions.Values.Select(entry => entry.Session).ToList();

    private sealed class SessionDetach(Action detach) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            detach();
        }
    }
}
