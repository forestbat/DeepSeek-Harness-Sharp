using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Runtime;
using Dsh.Transport;
using StreamJsonRpc;

namespace Dsh.Host;

/** Serves 0.5.0 mesh, collaboration rooms, discovery/trust and external channel ingress over RemoteHost. */
public sealed class HarnessRemoteMeshBackend : IRemoteMeshBackend, IDisposable
{
    private readonly Context _ctx;
    private readonly HostBroadcast _broadcast = new();
    private readonly Lock _cacheGate = new();
    private readonly IDisposable? _busSubscription;
    private IReadOnlyList<DiscoveredAgentCard> _cachedDiscovered = [];
    private IReadOnlyList<RemoteExecutionTarget> _cachedTargets = [];

    public HarnessRemoteMeshBackend(Context ctx)
    {
        _ctx = ctx;
        if (Bus is { } bus)
        {
            _busSubscription = bus.Subscribe(
                new AgentEventSubscription($"mesh-host-{Guid.NewGuid():N}", default, null, WakeSubscriber: false),
                runtimeEvent =>
                {
                    _ = PublishChangeAsync("agents", runtimeEvent.AgentId.Value);
                    return ValueTask.CompletedTask;
                });
        }
    }

    private IPersistentAgentManager? Agents => _ctx.Get<IPersistentAgentManager>(IPersistentAgentManager.ServiceName, strict: false);
    private IAgentMessageBus? Bus => _ctx.Get<IAgentMessageBus>(IAgentMessageBus.ServiceName, strict: false);
    private IAgentExecutionRouter? Router => _ctx.Get<IAgentExecutionRouter>(IAgentExecutionRouter.ServiceName, strict: false);
    private ICollaborationRoomService? Rooms => _ctx.Get<ICollaborationRoomService>(ICollaborationRoomService.ServiceName, strict: false);
    private IAgentDiscoveryService? Discovery => _ctx.Get<IAgentDiscoveryService>(IAgentDiscoveryService.ServiceName, strict: false);
    private IExternalChannelRouter? Channels => _ctx.Get<IExternalChannelRouter>(IExternalChannelRouter.ServiceName, strict: false);

    public bool SupportsMesh => Agents is not null || Bus is not null || Router is not null;
    public bool SupportsRooms => Rooms is not null;
    public bool SupportsDiscovery => Discovery is not null;
    public bool SupportsChannels => Channels is not null;

    public async Task<HostConnectionIdentity?> ResolveInviteAsync(
        string inviteToken,
        string? principalId,
        string? displayName,
        CancellationToken cancellationToken)
    {
        if (Rooms is not { } rooms || string.IsNullOrWhiteSpace(inviteToken))
            return null;
        var effectiveId = string.IsNullOrWhiteSpace(principalId) ? $"remote-{Guid.NewGuid():N}" : principalId;
        var joiner = new MessagePrincipal(
            PrincipalKind.RemoteUser,
            effectiveId,
            string.IsNullOrWhiteSpace(displayName) ? effectiveId : displayName);
        try
        {
            var room = await rooms.RedeemInviteAsync(inviteToken, joiner, cancellationToken: cancellationToken).ConfigureAwait(false);
            var participant = room.Participants.FirstOrDefault(item => string.Equals(item.PrincipalId, effectiveId, StringComparison.Ordinal));
            var role = participant?.Role ?? RoomRole.Observer;
            await PublishChangeAsync("rooms", room.Id.Value, cancellationToken).ConfigureAwait(false);
            return new HostConnectionIdentity(effectiveId, role, room.Id, room.Policy.AllowCollaboratorApprovals);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<HostSubscription> SubscribeAsync(CancellationToken cancellationToken)
    {
        var snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return _broadcast.Subscribe([HostNotification.Create(
            MeshProtocol.NotificationMeshChanged,
            new RemoteMeshChange("snapshot", snapshot),
            MeshProtocolJsonContext.Default.RemoteMeshChange)]);
    }

    public Task<IReadOnlyList<AgentProfile>> ListAgentsAsync(CancellationToken cancellationToken)
        => Task.FromResult(RequireAgents().List());

    public async Task<AgentProfile> CreateAgentAsync(AgentProfileRegistration request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        var profile = await RequireAgents().CreateAsync(request, cancellationToken).ConfigureAwait(false);
        await PublishChangeAsync("agents", profile.AgentId.Value, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task<AgentProfile> WakeAgentAsync(string agentId, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        var agents = RequireAgents();
        var id = AgentId.Create(agentId);
        await agents.WakeAsync(id, cancellationToken).ConfigureAwait(false);
        var profile = agents.Get(id) ?? throw new LocalRpcException($"unknown agent: {agentId}") { ErrorCode = JsonRpcException.InvalidParams };
        await PublishChangeAsync("agents", profile.AgentId.Value, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public async Task<bool> SuspendAgentAsync(string agentId, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        await RequireAgents().SuspendAsync(AgentId.Create(agentId), cancellationToken).ConfigureAwait(false);
        await PublishChangeAsync("agents", agentId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<AgentDeliveryResult> SendToAgentAsync(RemoteMeshSendRequest request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        var bus = RequireBus();
        SessionId? sessionId = string.IsNullOrWhiteSpace(request.TargetSessionId) ? null : SessionId.Create(request.TargetSessionId);
        var envelope = new AgentEnvelope(
            Guid.NewGuid().ToString("N"),
            AgentId.Create(request.AgentId),
            sessionId,
            PrincipalOf(caller),
            TrustLevelOf(caller),
            request.Intent,
            request.Body,
            request.CorrelationId,
            request.IdempotencyKey,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            request.WakeIfSuspended);
        var result = await bus.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
        await PublishChangeAsync("agents", request.AgentId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken)
    {
        if (Router is not { } router)
            return [];
        var targets = await router.ListTargetsAsync(cancellationToken).ConfigureAwait(false);
        lock (_cacheGate)
            _cachedTargets = targets;
        return targets;
    }

    public Task<IReadOnlyList<CollaborationRoom>> ListRoomsAsync(CancellationToken cancellationToken)
        => Task.FromResult(RequireRooms().ListRooms());

    public async Task<CollaborationRoom> CreateRoomAsync(RemoteRoomCreateRequest request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        var room = await RequireRooms().CreateRoomAsync(request.Name, PrincipalOf(caller), request.Policy, cancellationToken).ConfigureAwait(false);
        await PublishChangeAsync("rooms", room.Id.Value, cancellationToken).ConfigureAwait(false);
        return room;
    }

    public async Task<RoomInvite> CreateInviteAsync(RemoteRoomInviteRequest request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        var ttl = request.TtlSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
        return await RequireRooms().CreateInviteAsync(RoomId.Create(request.RoomId), PrincipalOf(caller), request.Role, ttl, request.MaxUses, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CollaborationRoom> JoinRoomAsync(RemoteRoomJoinRequest request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        AgentId? agentId = string.IsNullOrWhiteSpace(request.AgentId) ? null : AgentId.Create(request.AgentId);
        var room = await RequireRooms().RedeemInviteAsync(request.Token, PrincipalOf(caller), agentId, cancellationToken).ConfigureAwait(false);
        await PublishChangeAsync("rooms", room.Id.Value, cancellationToken).ConfigureAwait(false);
        return room;
    }

    public async Task<RoomMessage> PostRoomMessageAsync(RemoteRoomPostRequest request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        AgentId? targetAgentId = string.IsNullOrWhiteSpace(request.TargetAgentId) ? null : AgentId.Create(request.TargetAgentId);
        var message = await RequireRooms().PostMessageAsync(
            RoomId.Create(request.RoomId),
            PrincipalOf(caller),
            request.Body,
            targetAgentId,
            request.ReplyToMessageId,
            cancellationToken).ConfigureAwait(false);
        await PublishChangeAsync("rooms", request.RoomId, cancellationToken).ConfigureAwait(false);
        return message;
    }

    public Task<IReadOnlyList<RoomMessage>> ReadRoomMessagesAsync(RemoteRoomMessagesRequest request, CancellationToken cancellationToken)
        => Task.FromResult(RequireRooms().ReadMessages(RoomId.Create(request.RoomId), request.AfterSeq, request.Limit));

    public async Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAgentsAsync(RemoteDiscoveryQuery? request, CancellationToken cancellationToken)
    {
        var cards = await RequireDiscovery().DiscoverAsync(request?.Query, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request?.Query))
        {
            lock (_cacheGate)
                _cachedDiscovered = cards;
        }
        return cards;
    }

    public async Task<AgentTrustRecord> GrantTrustAsync(RemoteTrustGrantRequest request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        var record = RequireDiscovery().GrantTrust(request.CardId, request.Permissions, caller.PrincipalId, request.Note);
        await PublishChangeAsync("discovery", record.CardId, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<AgentTrustRecord> RevokeTrustAsync(RemoteTrustRevokeRequest request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        var record = RequireDiscovery().RevokeTrust(request.CardId, caller.PrincipalId, request.Reason);
        await PublishChangeAsync("discovery", record.CardId, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public Task<IReadOnlyList<AgentTrustRecord>> ListTrustAsync(CancellationToken cancellationToken)
        => Task.FromResult(RequireDiscovery().ListTrust());

    public Task<IReadOnlyList<ChannelBinding>> ListChannelsAsync(CancellationToken cancellationToken)
        => Task.FromResult(RequireChannels().ListBindings());

    public async Task<ChannelBinding> BindChannelAsync(ChannelBinding request, HostMeshCaller caller, CancellationToken cancellationToken)
    {
        var binding = RequireChannels().Bind(request);
        await PublishChangeAsync("channels", binding.BindingId, cancellationToken).ConfigureAwait(false);
        return binding;
    }

    public Task<IReadOnlyList<AutomationTriggerRule>> ListTriggersAsync(CancellationToken cancellationToken)
        => Task.FromResult(RequireChannels().ListTriggers());

    private async Task PublishChangeAsync(string area, string? entityId = null, CancellationToken cancellationToken = default)
    {
        if (_broadcast.SubscriberCount == 0)
            return;
        var snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
        _broadcast.Publish(HostNotification.Create(
            MeshProtocol.NotificationMeshChanged,
            new RemoteMeshChange(area, snapshot, entityId),
            MeshProtocolJsonContext.Default.RemoteMeshChange));
    }

    private Task<RemoteMeshSnapshot> BuildSnapshotAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DiscoveredAgentCard> discovered;
        IReadOnlyList<RemoteExecutionTarget> targets;
        lock (_cacheGate)
        {
            discovered = _cachedDiscovered;
            targets = _cachedTargets;
        }
        return Task.FromResult(new RemoteMeshSnapshot(
            Agents?.List() ?? [],
            targets,
            Rooms?.ListRooms() ?? [],
            discovered,
            Discovery?.ListTrust() ?? [],
            Channels?.ListBindings() ?? [],
            Channels?.ListTriggers() ?? []));
    }

    private static MessagePrincipal PrincipalOf(HostMeshCaller caller) => caller.Role switch
    {
        RoomRole.Owner => new(PrincipalKind.Human, caller.PrincipalId, caller.ClientName ?? caller.PrincipalId),
        RoomRole.AgentParticipant => new(PrincipalKind.Agent, caller.PrincipalId, caller.ClientName ?? caller.PrincipalId),
        _ => new(PrincipalKind.RemoteUser, caller.PrincipalId, caller.ClientName ?? caller.PrincipalId),
    };

    private static MessageTrustLevel TrustLevelOf(HostMeshCaller caller) => caller.Role switch
    {
        RoomRole.Owner => MessageTrustLevel.OwnerHuman,
        RoomRole.AgentParticipant => MessageTrustLevel.TrustedPeerAgent,
        _ => MessageTrustLevel.DelegatedHuman,
    };

    private IPersistentAgentManager RequireAgents()
        => Agents ?? throw new LocalRpcException("persistent agent manager is unavailable") { ErrorCode = JsonRpcException.MethodNotFound };

    private IAgentMessageBus RequireBus()
        => Bus ?? throw new LocalRpcException("agent message bus is unavailable") { ErrorCode = JsonRpcException.MethodNotFound };

    private ICollaborationRoomService RequireRooms()
        => Rooms ?? throw new LocalRpcException("collaboration rooms service is unavailable") { ErrorCode = JsonRpcException.MethodNotFound };

    private IAgentDiscoveryService RequireDiscovery()
        => Discovery ?? throw new LocalRpcException("agent discovery service is unavailable") { ErrorCode = JsonRpcException.MethodNotFound };

    private IExternalChannelRouter RequireChannels()
        => Channels ?? throw new LocalRpcException("external channel router is unavailable") { ErrorCode = JsonRpcException.MethodNotFound };

    public void Dispose() => _busSubscription?.Dispose();
}
