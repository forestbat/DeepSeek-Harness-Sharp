
namespace Dsh.Notifications;

/** What needs attention; only reflects process and interaction state, never whether a task is finished. */
public enum NotificationKind
{
    WaitingForInput,
    ApprovalRequest,
    Question,
    SessionExited,
    Error,
    Info,
}

/** Where clicking the notification should lead. */
public enum NotificationView
{
    Session,
    PendingRequest,
    Machine,
}

/** Jump target; RequestId names the pending approval or question when View is PendingRequest. */
public sealed record NotificationTarget(NotificationView View, string? RequestId = null);

/**
 * One notification from a machine, optionally scoped to a session.
 * Id is assigned by the producer so replays after a reconnect are recognised as duplicates.
 */
public sealed record NotificationRecord(
    string Id,
    string MachineId,
    string? SessionId,
    NotificationKind Kind,
    string Title,
    string? Body,
    DateTimeOffset CreatedAt,
    NotificationTarget Target,
    bool IsRead = false);

/** Null criteria match everything. */
public sealed record NotificationFilter(string? MachineId = null, string? SessionId = null, bool UnreadOnly = false)
{
    public static NotificationFilter All { get; } = new();

    public bool Matches(NotificationRecord record)
        => (MachineId is null || record.MachineId == MachineId)
           && (SessionId is null || record.SessionId == SessionId)
           && (!UnreadOnly || !record.IsRead);
}

