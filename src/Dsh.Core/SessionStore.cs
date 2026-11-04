using Dsh.Runtime;
using Dsh.Llm;

namespace Dsh.Core;

/**
 * 活动会话登记表。
 *
 * 会话创建是真并发的 —— 一条消息里并发 spawn 子代理就会同时 Create —— 所以字典访问一律走 _sync。
 * "登记 + 公告"必须是同一个原子操作: 旧实现把它拆成 Enter / Announce 让调用方自己拼,
 * 中间任何一次字典扩容都可能让 Announce 读不到刚写进去的键, 于是抛
 * `session "..." is not entered in the store`(或者更糟: 无锁并发写损坏字典内部状态)。
 */
public sealed class SessionStore(Context ctx) : Service(ctx, ServiceName)
{
    public const string ServiceName = "sessions";

    private sealed class Entry
    {
        public required Session Session { get; init; }
        public required IDisposable Detach { get; init; }
    }

    /** 保护 _sessions。事件一律在锁外发出: 订阅者回调可能再打回本 store。 */
    private readonly Lock _sync = new();
    private readonly Dictionary<SessionId, Entry> _sessions = [];

    public Session Create(SessionId? id = null, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
    {
        var session = Prepare(id, seed, header, inheritedEventCount);
        Enter(session);
        return session;
    }

    public Session Prepare(SessionId? id = null, IReadOnlyList<SessionEvent>? seed = null, SessionHeader? header = null, long? inheritedEventCount = null)
        => Session.Create(id ?? SessionId.Create(Guid.NewGuid().ToString()), seed, header, inheritedEventCount);

    /**
     * 登记会话并公告其创建。已登记则抛异常 —— 调用方要么用 TryEnter 表达"已在店里就是 no-op",
     * 要么就是真的重复登记(那是 bug, 需要炸出来)。
     */
    public IDisposable Enter(Session session)
        => EnterCore(session) ?? throw new InvalidOperationException($"session \"{session.Id}\" is already live in the store");

    /** 登记并公告, 已登记时返回 null。检查与插入在同一把锁内, 消除 check-then-act 竞态。 */
    public IDisposable? TryEnter(Session session) => EnterCore(session);

    private IDisposable? EnterCore(Session session)
    {
        Action<Session, SessionEvent> forward = (source, sessionEvent) => PublishEvent(source, sessionEvent);
        Action<Session, long> truncated = (source, eventCount) =>
            Ctx.Events.Emit(Ctx, new SessionTruncateNotification(source, eventCount));
        var detach = new SessionDetach(() =>
        {
            session.Appended -= forward;
            session.Truncated -= truncated;
            Detach(session);
        });
        lock (_sync)
        {
            if (!_sessions.TryAdd(session.Id, new Entry { Session = session, Detach = detach }))
                return null;
            session.Appended += forward;
            session.Truncated += truncated;
        }
        // 公告在锁外: 持久化收到它就会回查本 store, 持锁发事件等于把锁交给订阅者。
        Ctx.Emit(new SessionCreatedNotification(session));
        return detach;
    }

    /**
     * 会话的持有者(agent)结束时释放:连同 ParentSession 指向它的子孙会话一起解绑并发出 SessionDisposedNotification,
     * 持久化据此关闭写句柄。子会话在父会话存续期间留在 store,供子代理树查询。
     */
    public void Release(SessionId id)
    {
        IReadOnlyList<SessionId> children;
        IDisposable detach;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out var entry))
                return;
            children = [.. _sessions.Values
                .Where(child => child.Session.Header.ParentSession == id)
                .Select(child => child.Session.Id)];
            detach = entry.Detach;
        }
        // 解绑与递归都在锁外: Detach 自己也要拿锁, 持锁递归会自锁。
        detach.Dispose();
        foreach (var child in children)
            Release(child);
    }

    private void PublishEvent(Session session, SessionEvent sessionEvent)
    {
        Ctx.Events.Emit(Ctx, new SessionEventNotification(session, sessionEvent));
    }

    private void Detach(Session session)
    {
        lock (_sync)
        {
            if (!_sessions.Remove(session.Id))
                return;
        }
        Ctx.Emit(new SessionDisposedNotification(session));
    }

    public async Task Flush(Session session)
        => await Ctx.Events.Parallel(Ctx, new SessionFlushNotification(session));

    public Session? Get(SessionId id)
    {
        lock (_sync)
            return _sessions.TryGetValue(id, out var entry) ? entry.Session : null;
    }

    public IReadOnlyList<Session> List()
    {
        lock (_sync)
            return [.. _sessions.Values.Select(entry => entry.Session)];
    }

    private sealed class SessionDetach(Action detach) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            detach();
        }
    }
}
