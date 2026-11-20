using Dsh.Runtime.Events;

namespace Dsh.Core;

public sealed record SessionCreatedNotification(Session Session) : INotification
{
    public static string EventName => "session/created";
}

public sealed record SessionDisposedNotification(Session Session) : INotification
{
    public static string EventName => "session/disposed";
}

public sealed record SessionEventNotification(Session Session, SessionEvent Event) : INotification
{
    public static string EventName => "session/event";
}

public sealed record SessionFlushNotification(Session Session) : INotification
{
    public static string EventName => "session/flush";
}

/**
 * 会话就地截断到 EventCount(D3 revert): 持久化需要重写日志并重置句柄游标。
 * At 是截断发生的时刻(epoch ms): 读者据此只丢掉"截断之前"的那些事件 ——
 * 截断之后新写入的事件可能复用同一个序号, 不能按序号一刀切。
 */
public sealed record SessionTruncateNotification(Session Session, long EventCount, long At = 0) : INotification
{
    public static string EventName => "session/truncate";
}
