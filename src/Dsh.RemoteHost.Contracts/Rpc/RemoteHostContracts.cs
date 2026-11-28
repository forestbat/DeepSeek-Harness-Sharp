using System.Collections.ObjectModel;
using Dsh.Core;
using Dsh.GitInspect;
using StreamJsonRpc;
using PolyType;

[assembly: ExportRpcContractProxies]

namespace Dsh.RemoteHost.Rpc;

/**
 * 远端工作区的强类型契约: 每个方法一处签名, 访问修饰与 DTO 由编译器校验。
 *
 * 方法名用 `[JsonRpcMethod]` 固定成线协议里的点号名, 与 HostProtocol 等常量一一对应;
 * 尾参数 CancellationToken 由 StreamJsonRpc 映射为 `$/cancelRequest`, 调用方不再自建取消表。
 * 只在这里声明契约, 实现分别落在 host 端与客户端代理上 —— 两侧对不齐就是编译错误。
 */

/** 握手、会话与决策: 每条连接都必须有的核心域。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IHostRpc
{
    [JsonRpcMethod(HostProtocol.MethodHello)]
    Task<HostHelloResponse> HelloAsync(HostHelloRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodPing)]
    Task<long> PingAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodInfo)]
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionList)]
    Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionCreate)]
    Task<RemoteSessionSummary> CreateSessionAsync(RemoteSessionCreateRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionResume)]
    Task<RemoteSessionSummary> ResumeSessionAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionSubscribe)]
    Task<RemoteSubscriptionRef> SubscribeAsync(RemoteSubscribeRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionUnsubscribe)]
    Task<bool> UnsubscribeAsync(RemoteSubscriptionRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionMessage)]
    Task<bool> SendMessageAsync(RemoteMessageRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionInterrupt)]
    Task<bool> InterruptAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionSnapshot)]
    Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(RemoteSessionSnapshotRequest request, CancellationToken cancellationToken = default);

    /** 日志字节切片: 宿主只切字节, 客户端自己解析(投影实现仍只有一份)。 */
    [JsonRpcMethod(HostProtocol.MethodSessionLogSlice)]
    Task<RemoteLogSlice> GetSessionLogSliceAsync(RemoteLogSliceRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodToolsList)]
    Task<IReadOnlyList<string>> ListToolsAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodSessionsList)]
    Task<IReadOnlyList<RemoteSessionSummary>> ListRegistryAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodPendingList)]
    Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodApprovalRespond)]
    Task<bool> RespondApprovalAsync(RemoteApprovalResponse request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.MethodQuestionRespond)]
    Task<bool> RespondQuestionAsync(RemoteQuestionResponse request, CancellationToken cancellationToken = default);
}

/** 可选的 Agent 域: 只在宿主机装了 Agent 适配器时通告。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IAgentRpc
{
    [JsonRpcMethod(AgentProtocol.MethodAgentCatalog)]
    Task<IReadOnlyList<RemoteAgentCatalogEntry>> CatalogAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(AgentProtocol.MethodAgentStart)]
    Task<RemoteSessionSummary> StartAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(AgentProtocol.MethodAgentPrompt)]
    Task<bool> PromptAsync(RemoteAgentPrompt request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(AgentProtocol.MethodAgentCancel)]
    Task<bool> CancelAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(AgentProtocol.MethodAgentStop)]
    Task<bool> StopAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(AgentProtocol.MethodAgentResume)]
    Task<RemoteSessionSummary> ResumeAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);
}

/** 文件传输域: 小文件一次读写, 大文件走 upload.begin/chunk/commit 与 download 分片。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IFilesRpc
{
    [JsonRpcMethod(FilesProtocol.MethodFileRead)]
    Task<RemoteFileContent> ReadFileAsync(RemoteFilePath request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFileWrite)]
    Task<bool> WriteFileAsync(RemoteFileContent request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFileList)]
    Task<RemoteDirectoryListing> ListDirectoryAsync(RemoteFilePath request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsStat)]
    Task<RemoteFileStat> StatAsync(RemoteFilePath request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsMkdir)]
    Task<bool> CreateDirectoryAsync(RemoteFilePath request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsRename)]
    Task<bool> RenameAsync(RemoteFsRename request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsDelete)]
    Task<bool> DeleteAsync(RemoteFsDelete request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsUploadBegin)]
    Task<RemoteUploadRef> BeginUploadAsync(RemoteUploadBegin request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsUploadChunk)]
    Task<long> UploadChunkAsync(RemoteUploadChunk request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsUploadCommit)]
    Task<RemoteFileStat> CommitUploadAsync(RemoteUploadCommit request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsUploadAbort)]
    Task<bool> AbortUploadAsync(RemoteUploadRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(FilesProtocol.MethodFsDownload)]
    Task<RemoteFileChunk> DownloadAsync(RemoteDownloadRequest request, CancellationToken cancellationToken = default);
}

/** Git 观察域。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IGitRpc
{
    [JsonRpcMethod(GitProtocol.MethodGitStatus)]
    Task<GitStatus> StatusAsync(RemoteGitRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(GitProtocol.MethodGitDiff)]
    Task<GitDiff> DiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(GitProtocol.MethodGitLog)]
    Task<IReadOnlyList<GitCommitSummary>> LogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(GitProtocol.MethodGitWorktreeList)]
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(RemoteGitRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(GitProtocol.MethodGitWorktreeAdd)]
    Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(GitProtocol.MethodGitWorktreeRemove)]
    Task<bool> RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken = default);
}

/** 出站网络配置域。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface INetworkRpc
{
    [JsonRpcMethod(NetworkProtocol.MethodOutboundGet)]
    Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(NetworkProtocol.MethodOutboundSet)]
    Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(NetworkProtocol.MethodOutboundProbe)]
    Task<RemoteOutboundProxyProbeResult> ProbeAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(NetworkProtocol.MethodOutboundProbeCancel)]
    Task<bool> CancelProbeAsync(RemoteOutboundProxyProbeCancelRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(NetworkProtocol.MethodReload)]
    Task<bool> ReloadAsync(CancellationToken cancellationToken = default);
}

/** 通知历史域。 */[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface INotificationsRpc
{
    [JsonRpcMethod(NotificationsProtocol.MethodNotifyList)]
    Task<RemoteNotifyPage> ListAsync(RemoteNotifyQuery? request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(NotificationsProtocol.MethodNotifyMarkRead)]
    Task<int> MarkReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken = default);
}

/** 插件管理域。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IPluginsRpc
{
    [JsonRpcMethod(PluginsProtocol.MethodPluginsList)]
    Task<IReadOnlyList<RemotePluginInfo>> ListAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(PluginsProtocol.MethodPluginPermission)]
    Task<string> SetPermissionAsync(RemotePluginPermissionRequest request, CancellationToken cancellationToken = default);
}

/** Pty 域: 输出走通知, 这里只管控制面。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IPtyRpc
{
    [JsonRpcMethod(PtyProtocol.MethodPtyStart)]
    Task<RemotePtyRef> StartAsync(RemotePtyStartRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(PtyProtocol.MethodPtyWrite)]
    Task<bool> WriteAsync(RemotePtyInput request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(PtyProtocol.MethodPtyStop)]
    Task<bool> StopAsync(RemotePtyRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(PtyProtocol.MethodPtyAttach)]
    Task<bool> AttachAsync(RemotePtyAttachRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(PtyProtocol.MethodPtyDetach)]
    Task<bool> DetachAsync(RemoteSubscriptionRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(PtyProtocol.MethodPtyResize)]
    Task<bool> ResizeAsync(RemotePtyResize request, CancellationToken cancellationToken = default);
}

/** 会话编辑域。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface ISessionEditingRpc
{
    [JsonRpcMethod(SessionEditingProtocol.MethodSessionCommand)]
    Task<RemoteCommandResult> ExecuteCommandAsync(RemoteCommandRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(SessionEditingProtocol.MethodSessionCommands)]
    Task<IReadOnlyList<RemoteCommandDescriptor>> ListCommandsAsync(RemoteCommandRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(SessionEditingProtocol.MethodSessionRename)]
    Task<bool> RenameAsync(RemoteSessionRenameRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(SessionEditingProtocol.MethodSessionDelete)]
    Task<bool> DeleteAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);

    /** 会话的用户消息时间线: 刻度按序号定位, 客户端据此在整段会话上画时间轴(不依赖已渲染多少)。 */
    [JsonRpcMethod(SessionEditingProtocol.MethodSessionTimeline)]
    Task<IReadOnlyList<RemoteTimelineEntry>> SessionTimelineAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(SessionEditingProtocol.MethodSessionApprovalPolicy)]
    Task<bool> SetApprovalPolicyAsync(RemoteSessionPolicyRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(SessionEditingProtocol.MethodSessionSubagents)]
    Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(RemoteSessionRef request, CancellationToken cancellationToken = default);
}

/** 服务端对客户端发起的推送: 实现方在 host 端, 由客户端对象订阅。 */[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IHostPush
{
    [JsonRpcMethod(HostProtocol.NotificationEvent)]
    Task EventAsync(RemoteEventFrame frame, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.NotificationPendingChanged)]
    Task PendingChangedAsync(RemotePendingChange change, CancellationToken cancellationToken = default);

    [JsonRpcMethod(HostProtocol.NotificationSessionsChanged)]
    Task SessionsChangedAsync(RemoteSessionChange change, CancellationToken cancellationToken = default);

    [JsonRpcMethod(NotificationsProtocol.NotificationNotifyChanged)]
    Task NotifyChangedAsync(RemoteNotificationChange change, CancellationToken cancellationToken = default);

    [JsonRpcMethod(PtyProtocol.NotificationPtyOutput)]
    Task PtyOutputAsync(RemotePtyFrame frame, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.NotificationMeshChanged)]
    Task MeshChangedAsync(RemoteMeshChange change, CancellationToken cancellationToken = default);
}

/** 0.5.0 通用 Agent 基础设施域：持久化 Agent、统一执行目标、多人协作房间、发现与外部通道。 */
[JsonRpcContract, GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IMeshRpc
{
    [JsonRpcMethod(MeshProtocol.MethodAgentsList)]
    Task<IReadOnlyList<AgentProfile>> ListAgentsAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodAgentsCreate)]
    Task<AgentProfile> CreateAgentAsync(AgentProfileRegistration request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodAgentsWake)]
    Task<AgentProfile> WakeAgentAsync(RemoteMeshAgentRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodAgentsSuspend)]
    Task<bool> SuspendAgentAsync(RemoteMeshAgentRef request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodAgentsSend)]
    Task<AgentDeliveryResult> SendToAgentAsync(RemoteMeshSendRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodTargetsList)]
    Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodRoomsList)]
    Task<IReadOnlyList<CollaborationRoom>> ListRoomsAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodRoomsCreate)]
    Task<CollaborationRoom> CreateRoomAsync(RemoteRoomCreateRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodRoomsInvite)]
    Task<RoomInvite> CreateInviteAsync(RemoteRoomInviteRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodRoomsJoin)]
    Task<CollaborationRoom> JoinRoomAsync(RemoteRoomJoinRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodRoomsPost)]
    Task<RoomMessage> PostRoomMessageAsync(RemoteRoomPostRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodRoomsMessages)]
    Task<IReadOnlyList<RoomMessage>> ReadRoomMessagesAsync(RemoteRoomMessagesRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodDiscoveryList)]
    Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAgentsAsync(RemoteDiscoveryQuery? request = null, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodDiscoveryTrust)]
    Task<AgentTrustRecord> GrantTrustAsync(RemoteTrustGrantRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodDiscoveryRevoke)]
    Task<AgentTrustRecord> RevokeTrustAsync(RemoteTrustRevokeRequest request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodDiscoveryTrustList)]
    Task<IReadOnlyList<AgentTrustRecord>> ListTrustAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodChannelsList)]
    Task<IReadOnlyList<ChannelBinding>> ListChannelsAsync(CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodChannelsBind)]
    Task<ChannelBinding> BindChannelAsync(ChannelBinding request, CancellationToken cancellationToken = default);

    [JsonRpcMethod(MeshProtocol.MethodChannelsTriggers)]
    Task<IReadOnlyList<AutomationTriggerRule>> ListTriggersAsync(CancellationToken cancellationToken = default);
}
