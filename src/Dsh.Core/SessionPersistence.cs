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
    void Flush();
    void Close();
}

public interface ISessionPersistence
{
    ISessionHandle Create(SessionHeader header, long? inheritedEventCount = null);
    ISessionHandle Open(SessionId id, SessionAccess access);
    SessionPersistenceSnapshot? Stat(SessionId id);
    IReadOnlyList<SessionPersistenceSnapshot> List();
}
