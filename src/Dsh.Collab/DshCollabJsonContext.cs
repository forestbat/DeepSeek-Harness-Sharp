using System.Text.Json.Serialization;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Collab;

internal sealed record CollabRoomEntry(
    CollaborationRoom Room,
    List<RoomInvite> Invites,
    long NextSeq);

internal sealed record CollabRoomsDocument(
    List<CollabRoomEntry> Rooms);

internal sealed record CollabRoomMessagesDocument(
    RoomId RoomId,
    List<RoomMessage> Messages);

internal sealed record AgentTrustStoreDocument(
    List<AgentTrustRecord> TrustRecords,
    List<DiscoveredAgentCard> ImportedCards);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CollabRoomEntry))]
[JsonSerializable(typeof(CollabRoomsDocument))]
[JsonSerializable(typeof(CollabRoomMessagesDocument))]
[JsonSerializable(typeof(AgentTrustStoreDocument))]
[JsonSerializable(typeof(CollaborationRoom))]
[JsonSerializable(typeof(IReadOnlyList<CollaborationRoom>))]
[JsonSerializable(typeof(RoomParticipant))]
[JsonSerializable(typeof(IReadOnlyList<RoomParticipant>))]
[JsonSerializable(typeof(RoomPolicy))]
[JsonSerializable(typeof(RoomInvite))]
[JsonSerializable(typeof(IReadOnlyList<RoomInvite>))]
[JsonSerializable(typeof(RoomMessage))]
[JsonSerializable(typeof(IReadOnlyList<RoomMessage>))]
[JsonSerializable(typeof(DiscoveredAgentCard))]
[JsonSerializable(typeof(IReadOnlyList<DiscoveredAgentCard>))]
[JsonSerializable(typeof(AgentTrustRecord))]
[JsonSerializable(typeof(IReadOnlyList<AgentTrustRecord>))]
[JsonSerializable(typeof(AgentTrustPermissions))]
[JsonSerializable(typeof(AgentTrustStatus))]
[JsonSerializable(typeof(RoomRole))]
[JsonSerializable(typeof(PrincipalKind))]
[JsonSerializable(typeof(AgentBackendKind))]
[JsonSerializable(typeof(RoomId))]
[JsonSerializable(typeof(AgentId))]
[JsonSerializable(typeof(SessionId))]
internal sealed partial class DshCollabJsonContext : JsonSerializerContext
{
}
