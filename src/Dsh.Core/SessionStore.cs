using Dsh.Runtime;
using Dsh.Llm;

namespace Dsh.Core;

public sealed class SessionStore(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "sessions";

    private sealed class Entry
    {
        public required Session Session { get; init; }
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
        var entry = new Entry { Session = session };
        _sessions[session.Id] = entry;
        Action<Session, SessionEvent> forward = (source, sessionEvent) => PublishEvent(source, sessionEvent);
        session.Appended += forward;
        Action<Session, long> truncated = (source, eventCount) =>
            Ctx.Events.Emit(Ctx, new SessionTruncateNotification(source, eventCount));
        session.Truncated += truncated;
        return new SessionDetach(() =>
        {
            session.Appended -= forward;
            session.Truncated -= truncated;
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
        Ctx.Emit(new SessionCreatedNotification(session));
    }

    private void PublishEvent(Session session, SessionEvent sessionEvent)
    {
        Ctx.Events.Emit(Ctx, new SessionEventNotification(session, sessionEvent));
    }

    private void Detach(Session session)
    {
        if (!_sessions.Remove(session.Id))
            return;
        Ctx.Emit(new SessionDisposedNotification(session));
    }

    public async Task Flush(Session session)
        => await Ctx.Events.Parallel(Ctx, new SessionFlushNotification(session));

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
