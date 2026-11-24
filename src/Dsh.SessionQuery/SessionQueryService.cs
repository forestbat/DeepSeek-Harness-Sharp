using Dsh.Core;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.SessionQuery;

/** 会话全文检索:活会话按事件数、历史会话按持久层 Revision 增量刷新内存索引。 */
public sealed class SessionQueryService : Service, IDisposable
{
    public const string ServiceName = "sessionQuery";

    private readonly SessionQueryIndex _index;
    private readonly Dictionary<string, string> _versions = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();


    public SessionQueryService(Context ctx) : base(ctx, ServiceName)
    {
        _index = new SessionQueryIndex();
        if (!_index.UsesFullText)
            Ctx.Logger.Warn("%s", "system SQLite FTS5 is unavailable: session search uses local text matching");

    }

    public IReadOnlyList<SessionQueryHit> Search(string query, int limit = 20)
    {
        lock (_gate)
        {
            Refresh();
            return _index.Search(query, limit);
        }
    }

    private void Refresh()
    {
        foreach (var session in Ctx.Get<SessionStore>(SessionStore.ServiceName, false)?.List() ?? [])
        {
            var version = $"live:{session.Seq}";
            if (IsCurrent(session.Id.Value, version))
                continue;
            _index.IndexSession(session);
            _versions[session.Id.Value] = version;
        }

        if (Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false) is not { } persistence)
            return;
        foreach (var snapshot in persistence.List())
        {
            var id = snapshot.Header.Id.Value;
            if (_versions.TryGetValue(id, out var known) && known.StartsWith("live:", StringComparison.Ordinal))
                continue;   // 活会话以内存为准
            var version = $"stored:{snapshot.Revision}";
            if (IsCurrent(id, version))
                continue;
            using var handle = persistence.Open(snapshot.Header.Id, SessionAccess.Read);
            var session = Session.FromRestore(snapshot.Header.Id, handle.Read(), snapshot.Header, snapshot.InheritedEventCount);
            _index.IndexSession(session);
            _versions[id] = version;
        }
    }

    private bool IsCurrent(string id, string version)
        => _versions.TryGetValue(id, out var known) && string.Equals(known, version, StringComparison.Ordinal);

    public void Dispose()
    {

        _index.Dispose();
    }
}
