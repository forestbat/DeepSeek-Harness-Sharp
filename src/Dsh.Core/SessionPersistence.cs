using Dsh.Llm;

namespace Dsh.Core;

public enum SessionAccess
{
    Read,
    Write,
}

public sealed record SessionPersistenceSnapshot
{
    public required SessionHeader Header { get; init; }
    public required string Revision { get; init; }
    public long? EventCount { get; init; }
    public long? SizeBytes { get; init; }
    public long InheritedEventCount { get; init; }
}

public interface ISessionHandle : IDisposable
{
    SessionId Id { get; }
    SessionHeader Header { get; }
    long InheritedEventCount { get; }
    SessionAccess Access { get; }
    IReadOnlyList<SessionEvent> Read(long offset = 0, long? length = null);
    void Append(IReadOnlyList<SessionEvent> events);
    /** 就地截断日志到 eventCount(D3 revert), 并把游标重置到该位置。 */
    void Truncate(long eventCount);
    void Flush();
    void Close();
}

public interface ISessionPersistence
{
    public const string ServiceName = "sessionPersistence";

    ISessionHandle Create(SessionHeader header, long? inheritedEventCount = null);
    ISessionHandle Open(SessionId id, SessionAccess access);
    SessionPersistenceSnapshot? Stat(SessionId id);
    IReadOnlyList<SessionPersistenceSnapshot> List();
    void Rename(SessionId id, string title);
    void Delete(SessionId id);
}

public static class SessionPersistenceMetadata
{
    /**
     * 空会话(还没有任何事件)默认不落盘; 改名是用户明确的动作, 此时先建出只含头的日志再写标题,
     * 否则标题只活在内存里, 重启后就丢了。
     */
    public static void RenameSession(this ISessionPersistence persistence, SessionHeader header, string title)
    {
        if (persistence.Stat(header.Id) is null)
        {
            // Create 只登记内存待写; 必须 Flush 才会真正把(只有头的)日志写盘。
            using var handle = persistence.Create(header);
            handle.Flush();
        }
        persistence.Rename(header.Id, title);
    }
}
