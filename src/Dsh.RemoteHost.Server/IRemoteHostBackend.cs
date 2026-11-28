using Dsh.Core;
using Dsh.Ide;

namespace Dsh.RemoteHost;

/** A host composes its required session/decision/registry services and explicitly supplies optional domains. */
public interface IRemoteHostBackend
{
    HostPendingDecisions Pending { get; }
    HostSessionRegistry Sessions { get; }
    IRemoteSessionBackend Session { get; }
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken);

    IRemoteAgentBackend? Agent { get; }
    IRemotePtyBackend? Pty { get; }
    HostFileSystem? Files { get; }
    HostGitService? Git { get; }
    HostNotificationCenter? Notifications { get; }
    IRemoteSessionEditingBackend? SessionEditing { get; }
    IRemotePluginsBackend? Plugins { get; }
    IRemoteNetworkBackend? Network { get; }
    IRemoteEditorBackend? Editor => null;
    IRemoteMeshBackend? Mesh => null;
}

public sealed record HostMeshCaller(
    string ConnectionId,
    string PrincipalId,
    string? ClientName,
    RoomRole Role,
    RoomId? ScopedRoomId = null)
{
    public bool IsInviteScoped => ScopedRoomId is not null;
}

public sealed record HostConnectionIdentity(
    string PrincipalId,
    RoomRole Role,
    RoomId? RoomId = null,
    bool AllowCollaboratorApprovals = false);

public interface IRemoteMeshBackend
{
    bool SupportsMesh { get; }
    bool SupportsRooms { get; }
    bool SupportsDiscovery { get; }
    bool SupportsChannels { get; }

    Task<HostConnectionIdentity?> ResolveInviteAsync(string inviteToken, string? principalId, string? displayName, CancellationToken cancellationToken);
    Task<HostSubscription> SubscribeAsync(HostMeshCaller caller, CancellationToken cancellationToken);

    Task<bool> CanAccessSessionAsync(HostMeshCaller caller, string sessionId, string requiredRoomAction, CancellationToken cancellationToken)
        => Task.FromResult(!caller.IsInviteScoped);

    Task<IReadOnlyList<AgentProfile>> ListAgentsAsync(HostMeshCaller caller, CancellationToken cancellationToken);
    Task<AgentProfile> CreateAgentAsync(AgentProfileRegistration request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<AgentProfile> WakeAgentAsync(string agentId, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<bool> SuspendAgentAsync(string agentId, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<AgentDeliveryResult> SendToAgentAsync(RemoteMeshSendRequest request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CollaborationRoom>> ListRoomsAsync(HostMeshCaller caller, CancellationToken cancellationToken);
    Task<CollaborationRoom> CreateRoomAsync(RemoteRoomCreateRequest request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<RoomInvite> CreateInviteAsync(RemoteRoomInviteRequest request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<CollaborationRoom> JoinRoomAsync(RemoteRoomJoinRequest request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<RoomMessage> PostRoomMessageAsync(RemoteRoomPostRequest request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<IReadOnlyList<RoomMessage>> ReadRoomMessagesAsync(RemoteRoomMessagesRequest request, HostMeshCaller caller, CancellationToken cancellationToken);

    Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAgentsAsync(RemoteDiscoveryQuery? request, CancellationToken cancellationToken);
    Task<AgentTrustRecord> GrantTrustAsync(RemoteTrustGrantRequest request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<AgentTrustRecord> RevokeTrustAsync(RemoteTrustRevokeRequest request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentTrustRecord>> ListTrustAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<ChannelBinding>> ListChannelsAsync(CancellationToken cancellationToken);
    Task<ChannelBinding> BindChannelAsync(ChannelBinding request, HostMeshCaller caller, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutomationTriggerRule>> ListTriggersAsync(CancellationToken cancellationToken);
}

/** Required session execution and event stream. Subscribe installs its live listener before returning. */
public interface IRemoteSessionBackend
{
    Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken);
    Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken);
    Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken);
    /**
     * 一段历史: 从 fromEvent(绝对序号)起, 最多 maxEvents 条(0 表示由实现决定一页多大)。
     * 传范围而不是"整段历史再由服务端切": 百万级事件的会话每次翻页都整份物化一遍, 峰值内存与耗时都随历史长度走。
     */
    Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, long fromEvent, int maxEvents, CancellationToken cancellationToken);
    Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken);
    Task InterruptAsync(string sessionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken);
    Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken);
}

public interface IRemoteSessionEditingBackend
{
    Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken);
    Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);
    Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken);

    /** 会话的用户消息时间线(整段), 供客户端画时间轴。 */
    Task<IReadOnlyList<RemoteTimelineEntry>> ListSessionTimelineAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IRemoteAgentBackend
{
    Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken);
    Task<RemoteSessionSummary> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken);
    Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken);
    Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken);
    Task StopAgentAsync(string sessionId, CancellationToken cancellationToken);
    Task<RemoteSessionSummary> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken);
}

public interface IRemotePtyBackend
{
    Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken);
    Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken);
    Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken);
    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken);
    Task<RemoteHostStream<RemotePtyOutput>> AttachPtyAsync(string ptyId, CancellationToken cancellationToken);
}

public interface IRemotePluginsBackend
{
    Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken);
    Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken);
}

public interface IRemoteNetworkBackend
{
    Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken);
    Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken);
    Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken);
}

public interface IRemoteNetworkReloadBackend
{
    Task ReloadAsync(CancellationToken cancellationToken);
}

/**
 * 会话历史能以**日志字节**交付的后端: 头帧 + 覆盖 [fromEvent, fromEvent + maxEvents) 的帧原样字节。
 *
 * 支持就通告 `HostProtocol.LogSliceCapability`, 客户端于是自己解压解析(与读日志同一套代码);
 * 不支持则仍旧走事件快照。这套"能力按后端可选"的写法与 IRemoteNetworkReloadBackend 一致。
 */
public interface IRemoteLogSliceBackend
{
    Task<RemoteLogSlice> GetSessionLogSliceAsync(string sessionId, long fromEvent, int maxEvents, CancellationToken cancellationToken);
}

public interface IRemoteEditorBackend
{
    IEditorService Service { get; }
    Dsh.Ide.Bridge.EditorClientDispatcher? Clients { get; }
    HostPathPolicy? Paths => null;
}


