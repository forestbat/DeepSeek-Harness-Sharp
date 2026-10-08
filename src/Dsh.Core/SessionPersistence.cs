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
