using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

public static class SessionErrorCodes
{
    public const string AlreadyExists = "SessionAlreadyExists";
    public const string AlreadyOwned = "SessionAlreadyOwned";
    public const string FormatUnsupported = "SessionFormatUnsupported";
    public const string HandleClosed = "SessionHandleClosed";

    public const string Corruption = "SessionCorruption";
    public const string NotFound = "SessionNotFound";
    public const string ReadOnly = "SessionReadOnly";
    /** 发号票此刻在别人手里: 下一次写会重试, 不是终局。 */
    public const string WriteBusy = "SessionWriteBusy";
    /** 这一批的号被别人的写者接管过: 按日志重编号后照常写。 */
    public const string NumberStale = "SessionNumberStale";
}

public abstract class SessionPersistenceException : Exception
{
    protected SessionPersistenceException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public abstract string Code { get; }
}

public sealed class SessionPersistenceNotFoundException(SessionId sessionId)
    : SessionPersistenceException($"session \"{sessionId}\" not found")
{
    public SessionId SessionId { get; } = sessionId;
    public override string Code => SessionErrorCodes.NotFound;
}

public sealed class SessionAlreadyExistsException(SessionId sessionId)
    : SessionPersistenceException($"session \"{sessionId}\" already exists")
{
    public SessionId SessionId { get; } = sessionId;
    public override string Code => SessionErrorCodes.AlreadyExists;
}

public sealed class SessionAlreadyOwnedException(SessionId sessionId)
    : SessionPersistenceException($"session \"{sessionId}\" is already owned by an active write handle")
{
    public SessionId SessionId { get; } = sessionId;
    public override string Code => SessionErrorCodes.AlreadyOwned;
}

public sealed class SessionReadOnlyException(SessionId sessionId, string operation)
    : SessionPersistenceException($"session \"{sessionId}\": {operation} is not available on a read handle")
{
    public SessionId SessionId { get; } = sessionId;
    public override string Code => SessionErrorCodes.ReadOnly;
}

public sealed class SessionHandleClosedException(SessionId sessionId, string operation)
    : SessionPersistenceException($"session \"{sessionId}\": {operation} on a closed handle")
{
    public SessionId SessionId { get; } = sessionId;
    public override string Code => SessionErrorCodes.HandleClosed;
}

public sealed class SessionPersistenceCorruptionException(string message, Exception? innerException = null)
    : SessionPersistenceException(message, innerException)
{
    public override string Code => SessionErrorCodes.Corruption;
}

/**
 * 这一批的号已经过期: 日志末尾比我们发号时更靠后(别的写者接管了号并写进日志)。
 *
 * 这不是"忙"也不是损坏: 正确反应是按 `StartSeq` 把这批事件重编号后照常写进去,
 * 未落盘的那一段也只是换号, 一条都不丢。
 */
public sealed class SessionNumberStaleException(long startSeq, long got) : SessionPersistenceException(
    $"this batch starts at {got} but the log already holds {startSeq} event(s); renumbering to the log")
{
    public long StartSeq { get; } = startSeq;
    public override string Code => SessionErrorCodes.NumberStale;
}

/**
 * 发号权此刻不在自己手里(别的写者正持着这张票): **不是**失权, 更不是报废。
 *
 * 正确反应是"这一次先别写, 下次再试": 票有明确的到期时刻, 到期它自己就失效, 不需要任何人判死。
 */
public sealed class SessionWriteBusyException(int pid, string message, Exception? innerException = null)
    : SessionPersistenceException($"session write ticket is held by pid {pid}: {message}", innerException)
{
    public override string Code => SessionErrorCodes.WriteBusy;
}

public sealed record SessionLocation(string Kind, string Path);

public sealed class SessionFormatUnsupportedException : SessionPersistenceException
{
    public SessionFormatUnsupportedException(string message, SessionLocation? location = null)
        : base(message)
    {
        Location = location;
    }

    public SessionLocation? Location { get; }
    public override string Code => SessionErrorCodes.FormatUnsupported;
}

public static class SessionRefusals
{
    public static string FormatVersionRefusal(string id, long version) =>
        version > SessionHeader.SessionFormatVersion
            ? $"session \"{id}\" uses log format v{version}, but this harness reads only v{SessionHeader.SessionFormatVersion}: the log was written by a newer harness — upgrade the harness to open it"
            : $"session \"{id}\" uses log format v{version}, older than the supported v{SessionHeader.SessionFormatVersion}, and this build ships no upgrade path for it";
}
