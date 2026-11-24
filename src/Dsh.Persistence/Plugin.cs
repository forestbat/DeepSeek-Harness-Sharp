using Dsh.Runtime;
using Dsh.Runtime.Events;
using Dsh.Core;
using Dsh.Interaction;
using Dsh.Llm;
using Dsh.Plugins;

[assembly: DshPlugin(Dsh.Persistence.Plugin.Persistence)]

namespace Dsh.Persistence;

public sealed class Plugin(string packageName) : IDshPlugin
{
    internal const string Persistence = "@deepseek-ai/dsh-persistence";

    public string[] Inject => packageName switch
    {
        Persistence => [SessionStore.ServiceName, CommandsService.ServiceName],
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    public IDisposable Apply(Context ctx, object? config) => packageName switch
    {
        Persistence => RegisterPersistence(ctx, config),
        _ => throw new InvalidOperationException($"Unknown DSH package '{packageName}'."),
    };

    private static IDisposable RegisterPersistence(Context ctx, object? _)
    {
        var paths = DshPaths.FromContext(ctx)
            ?? throw new InvalidOperationException("dshPaths/dshHomePath is required for the persistence plugin");
        var persistence = new JsonlSessionPersistence(
            paths.SessionsPath,
            warn: message => ctx.LoggerFor("sessionPersistence").Warn(message));
        ctx.Provide(ISessionPersistence.ServiceName, persistence);
        var command = SessionCommand.Register(ctx, persistence);
        var wiring = WirePersistence(ctx, persistence);
        return new DisposableBundle(persistence, command, wiring);
    }

    internal static IDisposable WirePersistence(Context ctx, ISessionPersistence persistence)
    {
        // 句柄登记表被多线程同时读写: 创建/接管跑在创建者线程上, 而事件落盘跑在各自的回合线程上。
        // 无锁字典这样用会丢项甚至损坏内部状态; 把检查与插入并进同一把锁, 顺带堵住"两个线程都判定尚未登记
        // → 开两个写句柄 → ClaimWrite 抛 SessionAlreadyOwnedException"这条路径。
        var gate = new Lock();
        var handles = new Dictionary<SessionId, ISessionHandle>();
        // 终局性的落盘失败只报一次, 之后这个会话不再重试: 旧行为是每条事件都撞同一批错误,
        // 异常总线为每次失败打一份栈(2026-11-16 事故: 7 分钟、5489 条同样的栈)。原因留在 failures 里,
        // 由本轮的 flush 抛回给结束这一轮的人, 而不是静静丢事件。
        var failures = new Dictionary<SessionId, SessionPersistenceException>();
        // 可恢复的落盘受阻(写租约暂时不在自己手里): 句柄与缓冲都留着, 落盘按原样重试。
        // 与 failures 的区别是"不关句柄、不丢事件": 持有租约的进程一旦退出, 下一次落盘就能把写权收回来。
        var stalled = new Dictionary<SessionId, SessionPersistenceException>();
        // 受阻的会话只报一次, 避免每条事件刷一份同样的栈(2026-11-16 事故的教训)。
        var reportedStall = new HashSet<SessionId>();
        var created = ctx.On<SessionCreatedNotification>(notification =>
        {
            var session = notification.Session;
            lock (gate)
            {
                if (handles.ContainsKey(session.Id) || failures.ContainsKey(session.Id))
                    return;
                // 恢复既有会话时日志已在磁盘上, 只能接管不能重建;
                // 新会话先只留在内存里, 等第一条真实事件到来才建日志, 避免空会话落出垃圾文件。
                if (persistence.Stat(session.Id) is null)
                    return;
                try
                {
                    handles[session.Id] = Adopt(persistence, session);

                }
                catch (SessionPersistenceException error) when (IsTerminal(error))
                {
                    Latch(ctx, failures, session.Id, error);
                }
            }
        });
        var eventHandler = ctx.On<SessionEventNotification>(notification =>
        {
            ISessionHandle? handle;
            var materialized = false;
            var session = notification.Session;
            lock (gate)
            {
                if (failures.ContainsKey(session.Id))
                    return;
                if (!handles.TryGetValue(session.Id, out handle))
                {
                    try
                    {
                        handle = persistence.Stat(session.Id) is null
                            ? Materialize(persistence, session)
                            : Adopt(persistence, session);
                    }
                    catch (SessionPersistenceException error) when (IsTerminal(error))
                    {
                        Latch(ctx, failures, session.Id, error);
                        return;
                    }

                    handles[session.Id] = handle;
                    materialized = true;
                }
            }
            // 新建句柄时 Materialize 已经把当时的会话事件全量写入(含本条), 再追加会重复。
            // 写租约被别人拿走时**不关句柄、不丢缓冲**: 事件留在缓冲里, 下一次落盘先把写权收回来。
            // 写租约被别人拿走时也不关句柄: 事件留在缓冲里, 下一次落盘先试着把写权收回来。
            if (!materialized)
            {
                try
                {
                    handle.Append([notification.Event]);

                    lock (gate)
                    {
                        stalled.Remove(session.Id);
                        reportedStall.Remove(session.Id);
                    }
                }
                catch (SessionPersistenceException error) when (IsStall(error))
                {
                    Stall(ctx, stalled, reportedStall, session.Id, error);
                    return;
                }
                catch (SessionPersistenceException error) when (IsTerminal(error))
                {
                    lock (gate)
                        handles.Remove(session.Id);
                    CloseFailed(handle);
                    Latch(ctx, failures, session.Id, error);
                    return;
                }
            }

            DeriveTitle(ctx, persistence, session, notification.Event);
        });
        var flush = ctx.On<SessionFlushNotification>(notification =>
        {
            ISessionHandle? handle;
            SessionPersistenceException? failure;
            lock (gate)
            {
                failures.TryGetValue(notification.Session.Id, out failure);
                handles.TryGetValue(notification.Session.Id, out handle);
            }
            // 落盘已经报废: 把原因抛给结束这一轮的人, 不让这一轮看起来正常结束。
            if (failure is not null)
                throw failure;
            if (handle is null)
                return;
            // 受阻过(写租约在别人手里): 这一次落盘就是收回写权的机会 —— 收回来了就把这一整段补上。
            try
            {
                handle.Flush();

            }
            catch (SessionPersistenceException error) when (IsStall(error))
            {
                // 记下状态(只报一次), 再把原因交给结束这一轮的人: 事件仍在缓冲里, 不会丢。
                Stall(ctx, stalled, reportedStall, notification.Session.Id, error);
                throw;
            }
            lock (gate)
            {
                stalled.Remove(notification.Session.Id);
                reportedStall.Remove(notification.Session.Id);
            }
            // 折叠检查点: 折过一个读页边界才写一次(摊到 4096 条事件上是 0.2µs/条)。
            if (notification.Session.FoldSnapshotIfDue() is { } fold)
                persistence.SaveSurfaceFold(notification.Session.Id, fold);
        });

        var disposed = ctx.On<SessionDisposedNotification>(notification =>
        {
            ISessionHandle? handle;
            lock (gate)
                handles.Remove(notification.Session.Id, out handle);
            if (handle is null)
                return;
            // 收尾时补写一次检查点: 最后一个不满页也盖上, 下次打开直接从它的末尾接着折。
            if (notification.Session.FoldSnapshotOnClose() is { } fold)
                persistence.SaveSurfaceFold(notification.Session.Id, fold);
            // 关闭放在锁外: Close 会回调本类释放句柄登记, 持锁关闭等于把锁交给被调方。
            handle.Flush();
            handle.Close();
        });
        return new DisposableBundle(
            new CallbackDisposable(() => created()),
            new CallbackDisposable(() => eventHandler()),
            new CallbackDisposable(() => flush()),

            new CallbackDisposable(() => disposed()));
    }


    /** 重试也不会好的错误: 日志被别的写入方推进, 或会话已被别的写句柄占用。 */
    private static bool IsTerminal(SessionPersistenceException error)
        => error is SessionPersistenceCorruptionException
            or SessionAlreadyOwnedException;

    /**
     * 可以等的错误: 发号票此刻在别人手里。必须与"报废"分开 ——
     * 报废要停写并告诉用户, 而这种情形只要等对方放票(票到期也会自己失效)就能继续, 事件一条都不能丢。
     */
    private static bool IsStall(SessionPersistenceException error)
        => error is SessionWriteBusyException or SessionNumberStaleException;

    /** 记下"暂时写不进去", 只报一次: 之后每次落盘都会重试, 成功就自动恢复。 */
    private static void Stall(Context ctx, Dictionary<SessionId, SessionPersistenceException> stalled,
        HashSet<SessionId> reported, SessionId id, SessionPersistenceException error)
    {
        lock (stalled)
            stalled[id] = error;
        if (!reported.Add(id))
            return;
        ctx.LoggerFor("sessionPersistence").Warn(
            $"session \"{id}\": the write ticket is held by another writer right now, events are buffered and will be written once it frees up: {error.Message}");
    }

    /** 记录终局失败: 只报一次(不带栈), 之后该会话不再尝试落盘。 */
    private static void Latch(Context ctx, Dictionary<SessionId, SessionPersistenceException> failures,
        SessionId id, SessionPersistenceException error)
    {
        if (!failures.TryAdd(id, error))
            return;
        ctx.LoggerFor("sessionPersistence").Error(
            $"session \"{id}\": persistence failed, later events of this session are not saved: {error.Message}");
    }

    /** 失败的句柄关闭时还会再撞同一个错误; 已经记录过, 这里只求把进程内的写句柄让出来。 */
    private static void CloseFailed(ISessionHandle handle)
    {
        try
        {
            handle.Close();
        }
        catch (SessionPersistenceException)
        {
            // 同一次终局错误, 已经由 Latch 记录。
        }
    }

    /** 首条用户消息落盘时补一个标题(只做一次); 失败只记录, 标题是展示元数据, 不能影响会话推进。 */
    private static void DeriveTitle(Context ctx, ISessionPersistence persistence, Session session, SessionEvent sessionEvent)
    {
        if (session.Header.Title is { Length: > 0 })
            return;
        if (sessionEvent.Data is not UserMessagePayload userMessage)
            return;
        if (SessionTitleFromUserMessage.Derive(userMessage.Message) is not { } title)
            return;
        try
        {
            persistence.Rename(session.Id, title);
        }
        catch (Exception error)
        {
            ctx.LoggerFor("sessionPersistence").Warn($"session \"{session.Id}\": title persist failed: {error.Message}");
        }
        session.Rename(title);
    }

    private static ISessionHandle Materialize(ISessionPersistence persistence, Session session)
    {
        var handle = persistence.Create(session.Header, session.InheritedEventCount);
        handle.TailSource = from => [.. session.SnapshotEvents(from)];
        handle.RebaseSource = session.RebaseUnsaved;
        session.AttachSequencer(handle.Sequencer);
        var seed = session.SnapshotEvents();
        if (seed.Count > 0)
            handle.Append(seed);
        return handle;
    }

    /** 接管磁盘日志; 会话内存里可能多出日志没有的尾部(如中断轮的收尾事件)。 */
    private static ISessionHandle Adopt(ISessionPersistence persistence, Session session)
    {
        var handle = persistence.Open(session.Id, SessionAccess.Write);
        // 只比个数: 窗口化会话的事件不全在内存里, 为了比长度把它们全部读回来等于放弃窗口化。
        var storedCount = handle.Count;
        var ownCount = session.Seq - session.InheritedEventCount;
        // 落盘一旦落后, 句柄靠这句话把缺的那一段整段补回来(见 TryAlignToDiskLocked)。
        handle.TailSource = from => [.. session.SnapshotEvents(from)];
        handle.RebaseSource = session.RebaseUnsaved;
        session.AttachSequencer(handle.Sequencer);
        if (ownCount > storedCount)
            handle.Append([.. session.SnapshotEvents(session.InheritedEventCount + storedCount)]);
        return handle;
    }

    private sealed class DisposableBundle(params IDisposable[] disposables) : IDisposable
    {
        public void Dispose()
        {
            foreach (var disposable in disposables)
                disposable.Dispose();
        }
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
