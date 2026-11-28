using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.RemoteHost;

public static class MeshProtocol
{
    public const string CapabilityMesh = "agents.mesh";
    public const string CapabilityRooms = "collab.rooms";
    public const string CapabilityDiscovery = "agents.discovery";
    public const string CapabilityChannels = "channels.ingress";

    public const string MethodAgentsList = "host.mesh.agents.list";
    public const string MethodAgentsCreate = "host.mesh.agents.create";
    public const string MethodAgentsWake = "host.mesh.agents.wake";
    public const string MethodAgentsSuspend = "host.mesh.agents.suspend";
    public const string MethodAgentsSend = "host.mesh.agents.send";
    public const string MethodTargetsList = "host.mesh.targets.list";

    public const string MethodRoomsList = "host.mesh.rooms.list";
    public const string MethodRoomsCreate = "host.mesh.rooms.create";
    public const string MethodRoomsInvite = "host.mesh.rooms.invite";
    public const string MethodRoomsJoin = "host.mesh.rooms.join";
    public const string MethodRoomsPost = "host.mesh.rooms.post";
    public const string MethodRoomsMessages = "host.mesh.rooms.messages";

    public const string MethodDiscoveryList = "host.mesh.discovery.list";
    public const string MethodDiscoveryTrust = "host.mesh.discovery.trust";
    public const string MethodDiscoveryRevoke = "host.mesh.discovery.revoke";
    public const string MethodDiscoveryTrustList = "host.mesh.discovery.trustList";

    public const string MethodChannelsList = "host.mesh.channels.list";
    public const string MethodChannelsBind = "host.mesh.channels.bind";
    public const string MethodChannelsTriggers = "host.mesh.channels.triggers";

    public const string NotificationMeshChanged = "host.mesh.changed";

    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
    [
        MethodAgentsList,
        MethodAgentsCreate,
        MethodAgentsWake,
        MethodAgentsSuspend,
        MethodAgentsSend,
        MethodTargetsList,
        MethodRoomsList,
        MethodRoomsCreate,
        MethodRoomsInvite,
        MethodRoomsJoin,
        MethodRoomsPost,
        MethodRoomsMessages,
        MethodDiscoveryList,
        MethodDiscoveryTrust,
        MethodDiscoveryRevoke,
        MethodDiscoveryTrustList,
        MethodChannelsList,
        MethodChannelsBind,
        MethodChannelsTriggers,
    ]);
}

public sealed record RemoteMeshAgentRef(string AgentId);

public sealed record RemoteMeshSendRequest(
    string AgentId,
    string Body,
    AgentMessageIntent Intent = AgentMessageIntent.Task,
    string? TargetSessionId = null,
    string? CorrelationId = null,
    string? IdempotencyKey = null,
    bool WakeIfSuspended = true);

public sealed record RemoteRoomCreateRequest(string Name, RoomPolicy? Policy = null);

public sealed record RemoteRoomInviteRequest(string RoomId, RoomRole Role, int? TtlSeconds = null, int MaxUses = 1);

public sealed record RemoteRoomJoinRequest(string Token, string? AgentId = null);

public sealed record RemoteRoomPostRequest(string RoomId, string Body, string? TargetAgentId = null, string? ReplyToMessageId = null);

public sealed record RemoteRoomMessagesRequest(string RoomId, long AfterSeq = 0, int Limit = 100);

public sealed record RemoteDiscoveryQuery(string? Query = null);

public sealed record RemoteTrustGrantRequest(string CardId, AgentTrustPermissions Permissions, string? Note = null);

public sealed record RemoteTrustRevokeRequest(string CardId, string? Reason = null);

public sealed record RemoteMeshSnapshot(
    IReadOnlyList<AgentProfile> Agents,
    IReadOnlyList<RemoteExecutionTarget> Targets,
    IReadOnlyList<CollaborationRoom> Rooms,
    IReadOnlyList<DiscoveredAgentCard> Discovered,
    IReadOnlyList<AgentTrustRecord> TrustRecords,
    IReadOnlyList<ChannelBinding> Channels,
    IReadOnlyList<AutomationTriggerRule> Triggers);

public sealed record RemoteMeshChange(string Area, RemoteMeshSnapshot? Snapshot = null, string? EntityId = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentId))]
[JsonSerializable(typeof(RoomId))]
[JsonSerializable(typeof(SessionId))]
[JsonSerializable(typeof(AgentLifecycleState))]
[JsonSerializable(typeof(AgentBackendKind))]
[JsonSerializable(typeof(AgentProfile))]
[JsonSerializable(typeof(IReadOnlyList<AgentProfile>))]
[JsonSerializable(typeof(AgentProfileRegistration))]
[JsonSerializable(typeof(AgentOptions))]
[JsonSerializable(typeof(PrincipalKind))]
[JsonSerializable(typeof(MessageTrustLevel))]
[JsonSerializable(typeof(AgentMessageIntent))]
[JsonSerializable(typeof(AgentDeliveryStatus))]
[JsonSerializable(typeof(AgentDeliveryResult))]
[JsonSerializable(typeof(RemoteExecutionTarget))]
[JsonSerializable(typeof(IReadOnlyList<RemoteExecutionTarget>))]
[JsonSerializable(typeof(RoomRole))]
[JsonSerializable(typeof(RoomParticipant))]
[JsonSerializable(typeof(IReadOnlyList<RoomParticipant>))]
[JsonSerializable(typeof(RoomPolicy))]
[JsonSerializable(typeof(RoomInvite))]
[JsonSerializable(typeof(RoomMessage))]
[JsonSerializable(typeof(IReadOnlyList<RoomMessage>))]
[JsonSerializable(typeof(CollaborationRoom))]
[JsonSerializable(typeof(IReadOnlyList<CollaborationRoom>))]
[JsonSerializable(typeof(AgentTrustStatus))]
[JsonSerializable(typeof(AgentTrustPermissions))]
[JsonSerializable(typeof(DiscoveredAgentCard))]
[JsonSerializable(typeof(IReadOnlyList<DiscoveredAgentCard>))]
[JsonSerializable(typeof(AgentTrustRecord))]
[JsonSerializable(typeof(IReadOnlyList<AgentTrustRecord>))]
[JsonSerializable(typeof(ChannelBinding))]
[JsonSerializable(typeof(IReadOnlyList<ChannelBinding>))]
[JsonSerializable(typeof(AutomationTriggerRule))]
[JsonSerializable(typeof(IReadOnlyList<AutomationTriggerRule>))]
[JsonSerializable(typeof(RemoteMeshAgentRef))]
[JsonSerializable(typeof(RemoteMeshSendRequest))]
[JsonSerializable(typeof(RemoteRoomCreateRequest))]
[JsonSerializable(typeof(RemoteRoomInviteRequest))]
[JsonSerializable(typeof(RemoteRoomJoinRequest))]
[JsonSerializable(typeof(RemoteRoomPostRequest))]
[JsonSerializable(typeof(RemoteRoomMessagesRequest))]
[JsonSerializable(typeof(RemoteDiscoveryQuery))]
[JsonSerializable(typeof(RemoteTrustGrantRequest))]
[JsonSerializable(typeof(RemoteTrustRevokeRequest))]
[JsonSerializable(typeof(RemoteMeshSnapshot))]
[JsonSerializable(typeof(RemoteMeshChange))]
[JsonSerializable(typeof(IReadOnlyList<SessionId>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
public partial class MeshProtocolJsonContext : JsonSerializerContext;
