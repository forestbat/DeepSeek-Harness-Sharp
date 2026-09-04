using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Persistence;

public static class SessionErrorCodes
{
    public const string AlreadyExists = "SessionAlreadyExists";
    public const string AlreadyOwned = "SessionAlreadyOwned";
    public const string FormatUnsupported = "SessionFormatUnsupported";
    public const string HandleClosed = "SessionHandleClosed";
    public const string OwnershipLost = "SessionOwnershipLost";
    public const string Corruption = "SessionCorruption";
    public const string NotFound = "SessionNotFound";
    public const string ReadOnly = "SessionReadOnly";
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

public sealed class SessionOwnershipLostException(SessionId sessionId)
    : SessionPersistenceException($"session \"{sessionId}\": write ownership was lost; close this handle and reopen")
{
    public SessionId SessionId { get; } = sessionId;
    public override string Code => SessionErrorCodes.OwnershipLost;
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
