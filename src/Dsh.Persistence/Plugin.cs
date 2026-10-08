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
        var homePath = ctx.GetProp("dshHomePath") as string
            ?? throw new InvalidOperationException("dshHomePath is required for the persistence plugin");
        var persistence = new JsonlSessionPersistence(Path.Combine(homePath, "sessions"));
        ctx.Provide(ISessionPersistence.ServiceName, persistence);
        var command = SessionCommand.Register(ctx, persistence);
        var wiring = WirePersistence(ctx, persistence);
        return new DisposableBundle(persistence, command, wiring);
    }

    internal static IDisposable WirePersistence(Context ctx, ISessionPersistence persistence)
    {
        var handles = new Dictionary<SessionId, ISessionHandle>();
        var created = ctx.On<SessionCreatedNotification>(notification =>
        {
            var session = notification.Session;
            if (handles.ContainsKey(session.Id))
                return;
            // 恢复既有会话时日志已在磁盘上, 只能接管不能重建。
            handles[session.Id] = persistence.Stat(session.Id) is null
                ? Materialize(persistence, session)
                : Adopt(persistence, session);
        });
        var eventHandler = ctx.On<SessionEventNotification>(notification =>
        {
            if (handles.TryGetValue(notification.Session.Id, out var handle))
                handle.Append([notification.Event]);
            DeriveTitle(ctx, persistence, notification.Session, notification.Event);
        });
        var flush = ctx.On<SessionFlushNotification>(notification =>
        {
            if (handles.TryGetValue(notification.Session.Id, out var handle))
                handle.Flush();
        });
        var truncated = ctx.On<SessionTruncateNotification>(notification =>
        {
            if (handles.TryGetValue(notification.Session.Id, out var handle))
                handle.Truncate(notification.EventCount);
        });
        var disposed = ctx.On<SessionDisposedNotification>(notification =>
        {
            if (handles.Remove(notification.Session.Id, out var handle))
            {
                handle.Flush();
                handle.Close();
            }
        });
        return new DisposableBundle(
            new CallbackDisposable(() => created()),
            new CallbackDisposable(() => eventHandler()),
            new CallbackDisposable(() => flush()),
            new CallbackDisposable(() => truncated()),
            new CallbackDisposable(() => disposed()));
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
        var seed = session.SnapshotEvents();
        if (seed.Count > 0)
            handle.Append(seed);
        return handle;
    }

    /** 接管磁盘日志; 会话内存里可能多出日志没有的尾部(如中断轮的收尾事件)。 */
    private static ISessionHandle Adopt(ISessionPersistence persistence, Session session)
    {
        var handle = persistence.Open(session.Id, SessionAccess.Write);
        var stored = handle.Read();
        var own = session.OwnEvents();
        if (own.Count > stored.Count)
            handle.Append([.. own.Skip(stored.Count)]);
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
