using System.Text.Json.Serialization;

namespace Dsh.Core;

[JsonConverter(typeof(JsonStringEnumConverter<AgentTrustStatus>))]
public enum AgentTrustStatus
{
    Discovered,
    PendingApproval,
    Trusted,
    Revoked,
}

public sealed record AgentTrustPermissions(
    bool AllowMessaging = true,
    bool AllowTaskDelegation = false,
    bool AllowEventSubscription = true,
    bool AllowRoomParticipation = false,
    bool AllowFileAccess = false,
    bool AllowToolControl = false);

public sealed record DiscoveredAgentCard(
    string CardId,
    string Name,
    string Description,
    AgentBackendKind BackendKind,
    string Endpoint,
    IReadOnlyList<string> Capabilities,
    string? OwnerPrincipalId = null,
    string? PublicKey = null,
    AgentId? AgentId = null,
    string? SourceProvider = null,
    long DiscoveredAt = 0,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record AgentTrustRecord(
    string CardId,
    string Name,
    string Endpoint,
    AgentTrustStatus Status,
    AgentTrustPermissions Permissions,
    string UpdatedByPrincipalId,
    long UpdatedAt,
    string? Note = null);

public interface IAgentDiscoveryProvider
{
    string Name { get; }

    Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAsync(CancellationToken cancellationToken = default);
}

public interface IAgentDiscoveryService
{
    const string ServiceName = "agentDiscovery";

    IDisposable RegisterProvider(IAgentDiscoveryProvider provider);
    Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAsync(string? query = null, CancellationToken cancellationToken = default);
    DiscoveredAgentCard? ImportCard(string cardJson, string? source = null);
    string ExportLocalCardJson(AgentId agentId);
    IReadOnlyList<AgentTrustRecord> ListTrust();
    AgentTrustRecord? GetTrust(string cardId);
    AgentTrustRecord GrantTrust(string cardId, AgentTrustPermissions permissions, string actorPrincipalId, string? note = null);
    AgentTrustRecord RevokeTrust(string cardId, string actorPrincipalId, string? reason = null);
}
