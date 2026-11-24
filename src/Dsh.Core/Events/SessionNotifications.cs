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

/**
 * 未落盘的那一段整体换了号(别的写者接管了发号权): 从 FromSeq 起平移 Delta。
 *
 * 序号是客户端与本进程共享的键: 不广播这次平移, 界面就会拿着一套日志里不存在的号。
 * 典型处置是按新号平移该区间之后的卡片, 或者干脆按日志重放一遍。
 */
public sealed record SessionRenumberNotification(Session Session, long FromSeq, long Delta) : INotification
{
    public static string EventName => "session/renumber";
}

public sealed record SessionFlushNotification(Session Session) : INotification
{
    public static string EventName => "session/flush";
}

