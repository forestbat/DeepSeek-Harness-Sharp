using Dsh.Core;
using Dsh.GitInspect;
using Dsh.RemoteHost;

namespace Dsh.Workspace;

/** Typed agent capability; the connection owner controls its lifetime. */
public interface IWorkspaceAgents
{
    Task<IReadOnlyList<RemoteAgentCatalogEntry>> AgentCatalogAsync(CancellationToken cancellationToken = default);
    Task<RemoteSessionSummary> StartAgentAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken = default);
    Task PromptAgentAsync(string sessionId, string text, CancellationToken cancellationToken = default);
    Task CancelAgentAsync(string sessionId, CancellationToken cancellationToken = default);
    Task StopAgentAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<RemoteSessionSummary> ResumeAgentAsync(string sessionId, CancellationToken cancellationToken = default);
}

/** Typed files capability; the connection owner controls its lifetime. */
public interface IWorkspaceFiles
{
    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default);
    Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
    Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<RemoteFileStat> StatAsync(string path, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task RenameAsync(string from, string to, bool overwrite = false, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, bool recursive = false, CancellationToken cancellationToken = default);
    Task<RemoteUploadRef> BeginUploadAsync(string path, bool overwrite = true, CancellationToken cancellationToken = default);
    Task<long> UploadChunkAsync(string uploadId, long offset, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
    Task<RemoteFileStat> CommitUploadAsync(string uploadId, long? size = null, CancellationToken cancellationToken = default);
    Task AbortUploadAsync(string uploadId, CancellationToken cancellationToken = default);
    Task<RemoteFileChunk> DownloadChunkAsync(string path, long offset, int? length = null, CancellationToken cancellationToken = default);
    Task<RemoteFileStat> UploadFileAsync(string path, Stream source, bool overwrite = true, CancellationToken cancellationToken = default);
    Task<long> DownloadFileAsync(string path, Stream destination, CancellationToken cancellationToken = default);
}

/** Typed git capability; the connection owner controls its lifetime. */
public interface IWorkspaceGit
{
    Task<GitStatus> GitStatusAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<GitDiff> GitDiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitCommitSummary>> GitLogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string projectPath, CancellationToken cancellationToken = default);
    Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken = default);
    Task RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken = default);
}

/** Typed notifications capability; the connection owner controls its lifetime. */
public interface IWorkspaceNotifications
{
    Task<RemoteNotifyPage> ListNotificationsAsync(RemoteNotifyQuery? query = null, CancellationToken cancellationToken = default);
    Task<int> MarkNotificationsReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<RemoteNotificationChange> NotificationChangesAsync(CancellationToken cancellationToken = default);
}

/** Typed sessionediting capability; the connection owner controls its lifetime. */
public interface IWorkspaceSessionEditing
{
    Task<RemoteCommandResult> ExecuteSessionCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteCommandDescriptor>> ListSessionCommandsAsync(string sessionId, CancellationToken cancellationToken = default);
    Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default);
    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task SetSessionApprovalPolicyAsync(string sessionId, string policy, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(string rootSessionId, CancellationToken cancellationToken = default);

    /** 会话的用户消息时间线: GUI 的时间轴据此覆盖整段会话, 不依赖已渲染多少。 */
    Task<IReadOnlyList<RemoteTimelineEntry>> SessionTimelineAsync(string sessionId, CancellationToken cancellationToken = default);
}

/** Typed plugins capability; the connection owner controls its lifetime. */
public interface IWorkspacePlugins
{
    Task<IReadOnlyList<RemotePluginInfo>> ListPluginsAsync(CancellationToken cancellationToken = default);
    Task<string> SetPluginPermissionAsync(string package, string permission, bool allowed, CancellationToken cancellationToken = default);
}

/** Typed network capability; the connection owner controls its lifetime. */
public interface IWorkspaceNetwork
{
    Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default);
    Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default);
    Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
}

/** Typed pty capability; the connection owner controls its lifetime. */
public interface IWorkspaceTerminals
{
    Task<string> StartPtyAsync(RemotePtyStartRequest request, CancellationToken cancellationToken = default);
    Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default);
    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default);
    Task ResizePtyAsync(string ptyId, int columns, int rows, CancellationToken cancellationToken = default);
}

/** Host-owned sessions and decisions, independent of the transport used to reach them. */
public interface IWorkspaceHost
{
    HostHelloResponse Hello { get; }
    bool Supports(string capability);
    Task<long> PingAsync(CancellationToken cancellationToken = default);
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default);
    IWorkspaceAgents Agent { get; }
    /** Optional typed extensions are owned by their domain module and advertised by the connected Host. */
    T? GetCapability<T>() where T : class;
    IWorkspaceFiles Files { get; }
    IWorkspaceGit Git { get; }
    IWorkspaceNotifications Notifications { get; }
    IWorkspaceSessionEditing SessionEditing { get; }
    IWorkspacePlugins Plugins { get; }
    IWorkspaceNetwork Network { get; }
    IWorkspaceTerminals Pty { get; }
    Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default);
    Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default);
    Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken = default);
    Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default);
    Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<RemotePendingChange> PendingChangesAsync(CancellationToken cancellationToken = default);
    Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default, bool rememberForSession = false, bool cancelled = false);
    Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default, string status = "answered", string? reason = null);
    Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteSessionSummary>> ListHostSessionsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<RemoteSessionChange> HostSessionChangesAsync(CancellationToken cancellationToken = default);
}

/** Optional 0.5.0 agent mesh capability (agents, rooms, discovery, channels). */
public interface IWorkspaceMesh
{
    Task<IReadOnlyList<AgentProfile>> ListAgentsAsync(CancellationToken cancellationToken = default);
    Task<AgentProfile> CreateAgentAsync(AgentProfileRegistration request, CancellationToken cancellationToken = default);
    Task<AgentProfile> WakeAgentAsync(string agentId, CancellationToken cancellationToken = default);
    Task<bool> SuspendAgentAsync(string agentId, CancellationToken cancellationToken = default);
    Task<AgentDeliveryResult> SendToAgentAsync(RemoteMeshSendRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CollaborationRoom>> ListRoomsAsync(CancellationToken cancellationToken = default);
    Task<CollaborationRoom> CreateRoomAsync(string name, RoomPolicy? policy = null, CancellationToken cancellationToken = default);
    Task<RoomInvite> CreateInviteAsync(string roomId, RoomRole role, int? ttlSeconds = null, int maxUses = 1, CancellationToken cancellationToken = default);
    Task<CollaborationRoom> JoinRoomAsync(string token, string? agentId = null, CancellationToken cancellationToken = default);
    Task<RoomMessage> PostRoomMessageAsync(string roomId, string body, string? targetAgentId = null, string? replyToMessageId = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RoomMessage>> ReadRoomMessagesAsync(string roomId, long afterSeq = 0, int limit = 100, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAgentsAsync(string? query = null, CancellationToken cancellationToken = default);
    Task<AgentTrustRecord> GrantTrustAsync(string cardId, AgentTrustPermissions permissions, string? note = null, CancellationToken cancellationToken = default);
    Task<AgentTrustRecord> RevokeTrustAsync(string cardId, string? reason = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentTrustRecord>> ListTrustAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ChannelBinding>> ListChannelsAsync(CancellationToken cancellationToken = default);
    Task<ChannelBinding> BindChannelAsync(ChannelBinding binding, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AutomationTriggerRule>> ListTriggersAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<RemoteMeshChange> MeshChangesAsync(CancellationToken cancellationToken = default);
}
