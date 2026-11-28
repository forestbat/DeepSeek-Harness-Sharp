using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Core;

[JsonConverter(typeof(BrandJsonConverter<RoomId>))]
public readonly record struct RoomId(string Value) : IBrand<RoomId>
{
    public static RoomId Create(string value) => new(value);

    public static RoomId New() => new($"room-{Guid.NewGuid():N}");

    public override string ToString() => Value;
}

[JsonConverter(typeof(JsonStringEnumConverter<RoomRole>))]
public enum RoomRole
{
    Owner,
    Approver,
    Collaborator,
    Observer,
    AgentParticipant,
}

public static class RoomActions
{
    public const string Read = "read";
    public const string Post = "post";
    public const string PromptAgent = "prompt-agent";
    public const string Approve = "approve";
    public const string Invite = "invite";
    public const string ManageMembers = "manage-members";
}

public sealed record RoomParticipant(
    string PrincipalId,
    PrincipalKind Kind,
    string DisplayName,
    RoomRole Role,
    long JoinedAt,
    string? MachineId = null,
    AgentId? AgentId = null);

public sealed record RoomPolicy(
    bool AllowCollaboratorPrompts = true,
    bool AllowCollaboratorApprovals = false,
    bool AllowObserverRead = true,
    bool RequireExplicitAgentMention = true);

public sealed record RoomInvite(
    string InviteId,
    RoomId RoomId,
    string Token,
    RoomRole Role,
    string CreatedByPrincipalId,
    long CreatedAt,
    long ExpiresAt,
    int MaxUses = 1,
    int UsedCount = 0,
    bool Revoked = false);

public sealed record RoomMessage(
    string MessageId,
    RoomId RoomId,
    long Seq,
    string SenderPrincipalId,
    PrincipalKind SenderKind,
    string SenderDisplayName,
    string Body,
    long Timestamp,
    AgentId? TargetAgentId = null,
    string? ReplyToMessageId = null);

public sealed record CollaborationRoom(
    RoomId Id,
    string Name,
    string OwnerPrincipalId,
    RoomPolicy Policy,
    IReadOnlyList<RoomParticipant> Participants,
    long CreatedAt,
    long UpdatedAt);

public interface ICollaborationRoomService
{
    const string ServiceName = "collabRooms";

    Task<CollaborationRoom> CreateRoomAsync(string name, MessagePrincipal owner, RoomPolicy? policy = null, CancellationToken cancellationToken = default);
    CollaborationRoom? GetRoom(RoomId roomId);
    IReadOnlyList<CollaborationRoom> ListRooms();
    Task<RoomInvite> CreateInviteAsync(RoomId roomId, MessagePrincipal actor, RoomRole role, TimeSpan? ttl = null, int maxUses = 1, CancellationToken cancellationToken = default);
    Task<CollaborationRoom> RedeemInviteAsync(string token, MessagePrincipal joiner, AgentId? agentId = null, CancellationToken cancellationToken = default);
    Task<bool> RevokeInviteAsync(RoomId roomId, string inviteId, MessagePrincipal actor, CancellationToken cancellationToken = default);
    Task<CollaborationRoom> AddAgentAsync(RoomId roomId, MessagePrincipal actor, AgentId agentId, string? displayName = null, CancellationToken cancellationToken = default);
    Task<RoomMessage> PostMessageAsync(RoomId roomId, MessagePrincipal sender, string body, AgentId? targetAgentId = null, string? replyToMessageId = null, CancellationToken cancellationToken = default);
    IReadOnlyList<RoomMessage> ReadMessages(RoomId roomId, long afterSeq = 0, int limit = 100);
    bool CanPerform(RoomId roomId, string principalId, string action);
}
