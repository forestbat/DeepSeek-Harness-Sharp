using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.RemoteHost.Rpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional 0.5.0 Agent Mesh domain. */
public sealed class RemoteMeshClient : IWorkspaceMesh
{
    private const int MeshChangeBufferCapacity = 16;

    private readonly IMeshRpc _rpc;
    private readonly Lock _gate = new();
    private readonly HashSet<Channel<RemoteMeshChange>> _subscribers = [];
    private RemoteMeshChange? _latest;
    private bool _closed;
    private Exception? _closedError;

    internal RemoteMeshClient(RemoteHostClient host, IMeshRpc rpc) => _rpc = rpc;

    public Task<IReadOnlyList<AgentProfile>> ListAgentsAsync(CancellationToken cancellationToken = default)
        => _rpc.ListAgentsAsync(cancellationToken);

    public Task<AgentProfile> CreateAgentAsync(AgentProfileRegistration request, CancellationToken cancellationToken = default)
        => _rpc.CreateAgentAsync(request, cancellationToken);

    public Task<AgentProfile> WakeAgentAsync(string agentId, CancellationToken cancellationToken = default)
        => _rpc.WakeAgentAsync(new RemoteMeshAgentRef(agentId), cancellationToken);

    public Task<bool> SuspendAgentAsync(string agentId, CancellationToken cancellationToken = default)
        => _rpc.SuspendAgentAsync(new RemoteMeshAgentRef(agentId), cancellationToken);

    public Task<AgentDeliveryResult> SendToAgentAsync(RemoteMeshSendRequest request, CancellationToken cancellationToken = default)
        => _rpc.SendToAgentAsync(request, cancellationToken);

    public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default)
        => _rpc.ListTargetsAsync(cancellationToken);

    public Task<IReadOnlyList<CollaborationRoom>> ListRoomsAsync(CancellationToken cancellationToken = default)
        => _rpc.ListRoomsAsync(cancellationToken);

    public Task<CollaborationRoom> CreateRoomAsync(string name, RoomPolicy? policy = null, CancellationToken cancellationToken = default)
        => _rpc.CreateRoomAsync(new RemoteRoomCreateRequest(name, policy), cancellationToken);

    public Task<RoomInvite> CreateInviteAsync(string roomId, RoomRole role, int? ttlSeconds = null, int maxUses = 1, CancellationToken cancellationToken = default)
        => _rpc.CreateInviteAsync(new RemoteRoomInviteRequest(roomId, role, ttlSeconds, maxUses), cancellationToken);

    public Task<CollaborationRoom> JoinRoomAsync(string token, string? agentId = null, CancellationToken cancellationToken = default)
        => _rpc.JoinRoomAsync(new RemoteRoomJoinRequest(token, agentId), cancellationToken);

    public Task<RoomMessage> PostRoomMessageAsync(string roomId, string body, string? targetAgentId = null, string? replyToMessageId = null, CancellationToken cancellationToken = default)
        => _rpc.PostRoomMessageAsync(new RemoteRoomPostRequest(roomId, body, targetAgentId, replyToMessageId), cancellationToken);

    public Task<IReadOnlyList<RoomMessage>> ReadRoomMessagesAsync(string roomId, long afterSeq = 0, int limit = 100, CancellationToken cancellationToken = default)
        => _rpc.ReadRoomMessagesAsync(new RemoteRoomMessagesRequest(roomId, afterSeq, limit), cancellationToken);

    public Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAgentsAsync(string? query = null, CancellationToken cancellationToken = default)
        => _rpc.DiscoverAgentsAsync(new RemoteDiscoveryQuery(query), cancellationToken);

    public Task<AgentTrustRecord> GrantTrustAsync(string cardId, AgentTrustPermissions permissions, string? note = null, CancellationToken cancellationToken = default)
        => _rpc.GrantTrustAsync(new RemoteTrustGrantRequest(cardId, permissions, note), cancellationToken);

    public Task<AgentTrustRecord> RevokeTrustAsync(string cardId, string? reason = null, CancellationToken cancellationToken = default)
        => _rpc.RevokeTrustAsync(new RemoteTrustRevokeRequest(cardId, reason), cancellationToken);

    public Task<IReadOnlyList<AgentTrustRecord>> ListTrustAsync(CancellationToken cancellationToken = default)
        => _rpc.ListTrustAsync(cancellationToken);

    public Task<IReadOnlyList<ChannelBinding>> ListChannelsAsync(CancellationToken cancellationToken = default)
        => _rpc.ListChannelsAsync(cancellationToken);

    public Task<ChannelBinding> BindChannelAsync(ChannelBinding binding, CancellationToken cancellationToken = default)
        => _rpc.BindChannelAsync(binding, cancellationToken);

    public Task<IReadOnlyList<AutomationTriggerRule>> ListTriggersAsync(CancellationToken cancellationToken = default)
        => _rpc.ListTriggersAsync(cancellationToken);

    public async IAsyncEnumerable<RemoteMeshChange> MeshChangesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<RemoteMeshChange>(new BoundedChannelOptions(MeshChangeBufferCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        lock (_gate)
        {
            if (_latest is { } latest)
                channel.Writer.TryWrite(latest);
            if (_closed)
                channel.Writer.TryComplete(_closedError);
            else
                _subscribers.Add(channel);
        }
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return change;
        }
        finally
        {
            lock (_gate)
                _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        }
    }

    internal Task OnChangedAsync(RemoteMeshChange change)
    {
        lock (_gate)
        {
            if (_closed)
                return Task.CompletedTask;
            _latest = change;
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryWrite(change);
        }
        return Task.CompletedTask;
    }

    internal void OnClosed(Exception failure)
    {
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            _closedError = failure;
            foreach (var subscriber in _subscribers)
                subscriber.Writer.TryComplete(failure);
        }
    }
}
