using System.Runtime.CompilerServices;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.SessionGraph;

/**
 * 会话事件图: 节点与边都从会话日志派生, 日志仍是唯一真相源。
 * 活会话(在 SessionStore 里)以内存日志为准: 每次查询只吸收上次之后追加的事件, 截断时丢弃、下次查询按截断后的日志重建。
 * 其余会话从持久层日志构建, 按 Revision 缓存; 持久层重写日志(截断、改名)会换 Revision, 缓存随之失效。
 */
public sealed class SessionEventGraphService : Service, IDisposable
{
    public const string ServiceName = "sessionEventGraph";

    private readonly Lock _gate = new();
    private readonly ConditionalWeakTable<Session, SessionLogGraph> _live = new();
    private readonly Dictionary<SessionId, (string Revision, SessionLogGraph Graph)> _stored = [];
    private readonly Func<bool> _unsubscribeTruncate;

    public SessionEventGraphService(Context ctx) : base(ctx, ServiceName)
    {
        // 截断后 seq 会复用, 已吸收的事件不再是日志的前缀: 丢弃整张图。
        _unsubscribeTruncate = ctx.On<SessionTruncateNotification>(notification =>
        {
            lock (_gate)
                _live.Remove(notification.Session);
        }, new EventOptions { Global = true });
    }

    private SessionStore? Sessions => Ctx.Get<SessionStore>(SessionStore.ServiceName, false);

    private ISessionPersistence? Persistence => Ctx.Get<ISessionPersistence>(ISessionPersistence.ServiceName, false);

    public SessionEventNode? Node(SessionId session, long seq) => Read(session, graph => graph.Node(seq), null);

    public IReadOnlyList<SessionEventEdge> EdgesFrom(SessionId session, long seq) => Read(session, graph => graph.EdgesFrom(seq), []);

    public IReadOnlyList<SessionEventEdge> EdgesTo(SessionId session, long seq) => Read(session, graph => graph.EdgesTo(seq), []);

    public SessionLineage? Parent(SessionId session)
    {
        if (Sessions?.Get(session) is { } live)
            return LineageOf(live.Header, live.InheritedEventCount);
        return Persistence?.Stat(session) is { } snapshot ? LineageOf(snapshot.Header, snapshot.InheritedEventCount) : null;
    }

    public IReadOnlyList<SessionLineage> Children(SessionId session)
    {
        var snapshots = (Sessions?.List() ?? []).Select(live => (live.Header, live.InheritedEventCount))
            .Concat((Persistence?.List() ?? []).Select(snapshot => (snapshot.Header, snapshot.InheritedEventCount)));
        return
        [
            .. snapshots
                .DistinctBy(snapshot => snapshot.Header.Id)
                .Where(snapshot => snapshot.Header.ParentSession == session)
                .OrderBy(snapshot => snapshot.Header.CreatedAt)
                .ThenBy(snapshot => snapshot.Header.Id.Value, StringComparer.Ordinal)
                .Select(snapshot => LineageOf(snapshot.Header, snapshot.InheritedEventCount))
                .OfType<SessionLineage>(),
        ];
    }

    private SessionLineage? LineageOf(SessionHeader header, long inheritedEventCount)
    {
        if (header.ParentSession is not { } parent)
            return null;
        if (header.IsSubagent)
            return new SessionLineage(header.Id, SessionLineageKind.Subagent, parent,
                Read(parent, graph => graph.SpawnOf(header.Id)
                    ?? (header.ParentCallId is { } callId ? graph.OpenerOf(SessionEventRelations.ToolCall(callId)) : null), null));
        return new SessionLineage(header.Id, SessionLineageKind.Fork, parent, inheritedEventCount > 0 ? inheritedEventCount - 1 : null);
    }

    private T Read<T>(SessionId session, Func<SessionLogGraph, T> read, T missing)
    {
        if (Sessions?.Get(session) is { } live)
        {
            lock (_gate)
                return read(LiveGraph(live));
        }
        return StoredGraph(session) is { } stored ? read(stored) : missing;
    }

    private SessionLogGraph LiveGraph(Session session)
    {
        var log = session.SnapshotEvents();
        if (!_live.TryGetValue(session, out var graph) || graph.Count > log.Count)
        {
            graph = new SessionLogGraph(session.InheritedEventCount);
            _live.AddOrUpdate(session, graph);
        }
        graph.CatchUp(log);
        return graph;
    }

    private SessionLogGraph? StoredGraph(SessionId session)
    {
        if (Persistence is not { } persistence || persistence.Stat(session) is not { } snapshot)
            return null;
        lock (_gate)
        {
            if (_stored.TryGetValue(session, out var cached) && cached.Revision == snapshot.Revision)
                return cached.Graph;
        }
        // 解压整份日志可能较慢, 不占用锁, 以免阻塞写线程上的截断通知与其他会话的查询。
        var graph = new SessionLogGraph(snapshot.InheritedEventCount);
        using (var handle = persistence.Open(session, SessionAccess.Read))
            graph.CatchUp(handle.Read());
        lock (_gate)
            _stored[session] = (snapshot.Revision, graph);
        return graph;
    }

    public void Dispose() => _unsubscribeTruncate();
}
