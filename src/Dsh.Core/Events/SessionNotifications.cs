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

/** 会话就地截断到 EventCount(D3 revert): 持久化需要重写日志并重置句柄游标。 */
public sealed record SessionTruncateNotification(Session Session, long EventCount) : INotification
{
    public static string EventName => "session/truncate";
}
