using Dsh.Core;
using Dsh.GitInspect;
using Dsh.Notifications;
using System.Reflection;
using System.Text.Json.Serialization;


namespace Dsh.RemoteHost;

/**
 * 远端工作区协议: 本地 GUI/CLI 与远端 `dsharp host --serve` 之间的握手、能力与推送方法名。
 * 帧格式由 Dsh.Transport(JsonRpcPeer) 提供; 这里只定义方法名与 DTO。
 */
public static class HostProtocol
{
    private const char CommitSeparator = '+';

    /**
     * 线协议版本: 只在帧格式/握手/既有方法的语义发生不兼容变化时递增, 不匹配时握手显式拒绝。
     * 新增的方法、推送与可选字段不改版本, 由握手响应与 host.info 里的 capabilities 声明(见 HostProtocolCatalog)。
     */
    public const int Version = 2;

    /** Release identity shared by the host, client, Fleet and GUI; the build appends "+commit". */
    public static string ReleaseVersion { get; } = ReleaseVersionOf(typeof(HostProtocol).Assembly);

    public const string PendingBroadcastCapability = "pending.broadcast";
    public const string SessionRegistryCapability = "sessions.registry";

    /**
     * 一页会话历史的字节预算: 必须明显低于传输层单帧上限(FrameCodec, 16 MB), 才能给 JSON-RPC 外壳与头部字段留余量。
     * 超长会话的历史按它切页, 客户端凭 TotalEventCount 取完整段历史。
     */
    public const int SnapshotPageBytes = 12 * 1024 * 1024;

    public const string MethodHello = "host.hello";
    public const string MethodPing = "host.ping";
    public const string MethodInfo = "host.info";

    public const string MethodSessionList = "host.session.list";
    public const string MethodSessionCreate = "host.session.create";
    public const string MethodSessionResume = "host.session.resume";
    public const string MethodSessionSubscribe = "host.session.subscribe";
    public const string MethodSessionUnsubscribe = "host.session.unsubscribe";
    public const string MethodSessionMessage = "host.session.message";
    public const string MethodSessionInterrupt = "host.session.interrupt";
    public const string MethodSessionSnapshot = "host.session.snapshot";

    public const string MethodToolsList = "host.tools.list";
    public const string MethodApprovalRespond = "host.approval.respond";
    public const string MethodQuestionRespond = "host.question.respond";


    public const string MethodPendingList = "host.pending.list";
    public const string MethodSessionsList = "host.sessions.list";


    /** 服务端 -> 客户端 推送。 */
    public const string NotificationEvent = "host.event";
    public const string NotificationPendingChanged = "host.pending.changed";
    public const string NotificationSessionsChanged = "host.sessions.changed";

    private static string ReleaseVersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var cut = informational.IndexOf(CommitSeparator);
        var version = cut >= 0 ? informational[..cut] : informational;
        return version.Length > 0 ? version : "0.0.0";
    }
}


/** 握手请求: 客户端声明的协议版本与 token; ClientName 是客户端自报的显示名(如 "gui@laptop"), 用于标注由谁回答了审批。 */
public sealed record HostHelloRequest(string? Token, int ProtocolVersion, string? ClientName = null);

/** 握手响应: 服务端版本; Mismatch 非空即拒绝(附原因); ConnectionId 是本连接在 host 内的唯一标识。 */
public sealed record HostHelloResponse(
    bool Ok,
    int ProtocolVersion,
    string HostVersion,
    string? Mismatch,
    IReadOnlyList<string>? Capabilities = null,
    string? ConnectionId = null);

/** 服务端信息。 */
public sealed record HostInfo(int ProtocolVersion, string HostVersion, string Platform, string HomeRoot, IReadOnlyList<string>? Capabilities = null);

/** 源生成的 JSON 上下文(AOT/裁剪安全)。 */
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HostHelloRequest))]
[JsonSerializable(typeof(HostHelloResponse))]
[JsonSerializable(typeof(HostInfo))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(RemoteSessionSummary))]
[JsonSerializable(typeof(RemoteSessionEvent))]
[JsonSerializable(typeof(SessionHeader))]
[JsonSerializable(typeof(SessionEvent))]
[JsonSerializable(typeof(RemoteSessionSnapshot))]
[JsonSerializable(typeof(RemoteSessionSnapshotRequest))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(RemoteApprovalRequest))]
[JsonSerializable(typeof(RemoteApprovalResponse))]
[JsonSerializable(typeof(RemoteQuestionRequest))]
[JsonSerializable(typeof(RemoteQuestionItem))]
[JsonSerializable(typeof(RemoteQuestionOption))]
[JsonSerializable(typeof(RemoteQuestionResponse))]
[JsonSerializable(typeof(RemoteQuestionAnswerItem))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionItem>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionAnswerItem>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionOption>))]
[JsonSerializable(typeof(RemoteSessionCreateRequest))]
[JsonSerializable(typeof(RemoteSessionRef))]
[JsonSerializable(typeof(RemoteSubscribeRequest))]
[JsonSerializable(typeof(RemoteSubscriptionRef))]
[JsonSerializable(typeof(RemoteEventFrame))]
[JsonSerializable(typeof(RemoteMessageRequest))]
[JsonSerializable(typeof(RemoteImageBlock))]
[JsonSerializable(typeof(IReadOnlyList<RemoteImageBlock>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteSessionSummary>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(RemotePendingList))]
[JsonSerializable(typeof(RemotePendingResolved))]
[JsonSerializable(typeof(RemotePendingChange))]
[JsonSerializable(typeof(RemoteSessionSummary))]
[JsonSerializable(typeof(RemoteSessionChange))]
[JsonSerializable(typeof(IReadOnlyList<RemoteSessionSummary>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteApprovalRequest>))]
[JsonSerializable(typeof(IReadOnlyList<RemoteQuestionRequest>))]
public partial class HostProtocolJsonContext : JsonSerializerContext;




/** Optional Agent wire contract; method and capability values are stable. */
public static class AgentProtocol
{
    public const string Capability = "agent.sessions";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodAgentCatalog, MethodAgentStart, MethodAgentPrompt, MethodAgentCancel, MethodAgentStop, MethodAgentResume]);

    public const string MethodAgentCatalog = "host.agent.catalog";
    public const string MethodAgentStart = "host.agent.start";
    public const string MethodAgentPrompt = "host.agent.prompt";
    public const string MethodAgentCancel = "host.agent.cancel";
    public const string MethodAgentStop = "host.agent.stop";
    public const string MethodAgentResume = "host.agent.resume";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteAgentCatalogEntry))]
[JsonSerializable(typeof(IReadOnlyList<RemoteAgentCatalogEntry>))]
[JsonSerializable(typeof(RemoteAgentStartRequest))]
[JsonSerializable(typeof(RemoteAgentPrompt))]
[JsonSerializable(typeof(RemoteSessionSummary))]
[JsonSerializable(typeof(RemoteSessionRef))]
[JsonSerializable(typeof(bool))]
public partial class AgentProtocolJsonContext : JsonSerializerContext;




/** Optional Files wire contract; method and capability values are stable. */
public static class FilesProtocol
{
    public const string Capability = "fs.transfer";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodFileRead, MethodFileWrite, MethodFileList, MethodFsStat, MethodFsMkdir, MethodFsRename, MethodFsDelete,
            MethodFsUploadBegin, MethodFsUploadChunk, MethodFsUploadCommit, MethodFsUploadAbort, MethodFsDownload]);

    public const string MethodFileRead = "host.file.read";
    public const string MethodFileWrite = "host.file.write";
    public const string MethodFileList = "host.file.list";
    public const string MethodFsStat = "host.fs.stat";
    public const string MethodFsMkdir = "host.fs.mkdir";
    public const string MethodFsRename = "host.fs.rename";
    public const string MethodFsDelete = "host.fs.delete";
    public const string MethodFsUploadBegin = "host.fs.upload.begin";
    public const string MethodFsUploadChunk = "host.fs.upload.chunk";
    public const string MethodFsUploadCommit = "host.fs.upload.commit";
    public const string MethodFsUploadAbort = "host.fs.upload.abort";
    public const string MethodFsDownload = "host.fs.download";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteFilePath))]
[JsonSerializable(typeof(RemoteFileContent))]
[JsonSerializable(typeof(RemoteDirectoryEntry))]
[JsonSerializable(typeof(RemoteDirectoryListing))]
[JsonSerializable(typeof(IReadOnlyList<RemoteDirectoryEntry>))]
[JsonSerializable(typeof(RemoteFileStat))]
[JsonSerializable(typeof(RemoteFsRename))]
[JsonSerializable(typeof(RemoteFsDelete))]
[JsonSerializable(typeof(RemoteUploadBegin))]
[JsonSerializable(typeof(RemoteUploadRef))]
[JsonSerializable(typeof(RemoteUploadChunk))]
[JsonSerializable(typeof(RemoteUploadCommit))]
[JsonSerializable(typeof(RemoteDownloadRequest))]
[JsonSerializable(typeof(RemoteFileChunk))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(long))]
public partial class FilesProtocolJsonContext : JsonSerializerContext;



/** Optional Git wire contract; method and capability values are stable. */
public static class GitProtocol
{
    public const string Capability = "git.inspect";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodGitStatus, MethodGitDiff, MethodGitLog, MethodGitWorktreeList, MethodGitWorktreeAdd, MethodGitWorktreeRemove]);

    public const string MethodGitStatus = "host.git.status";
    public const string MethodGitDiff = "host.git.diff";
    public const string MethodGitLog = "host.git.log";
    public const string MethodGitWorktreeList = "host.git.worktree.list";
    public const string MethodGitWorktreeAdd = "host.git.worktree.add";
    public const string MethodGitWorktreeRemove = "host.git.worktree.remove";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteGitRequest))]
[JsonSerializable(typeof(RemoteGitDiffRequest))]
[JsonSerializable(typeof(RemoteGitLogRequest))]
[JsonSerializable(typeof(RemoteGitWorktreeAdd))]
[JsonSerializable(typeof(RemoteGitWorktreeRemove))]
[JsonSerializable(typeof(GitStatus))]
[JsonSerializable(typeof(GitDiff))]
[JsonSerializable(typeof(IReadOnlyList<GitCommitSummary>))]
[JsonSerializable(typeof(GitWorktree))]
[JsonSerializable(typeof(IReadOnlyList<GitWorktree>))]
[JsonSerializable(typeof(bool))]
public partial class GitProtocolJsonContext : JsonSerializerContext;



/** Optional target-host outbound network configuration contract. */
public static class NetworkProtocol
{
    public const string Capability = "network.outbound";
    public const string CancellableProbeCapability = "network.outbound.probe.cancel";
    public const string ReloadCapability = "network.reload";
    public const string MethodReload = "host.network.reload";
    public const int RevisionConflict = -32009;
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodOutboundGet, MethodOutboundSet, MethodOutboundProbe, MethodOutboundProbeCancel]);

    public const string MethodOutboundGet = "host.network.outbound.get";
    public const string MethodOutboundSet = "host.network.outbound.set";
    public const string MethodOutboundProbe = "host.network.outbound.probe";
    public const string MethodOutboundProbeCancel = "host.network.outbound.probe.cancel";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RemoteOutboundProxySettings))]
[JsonSerializable(typeof(RemoteOutboundProxyState))]
[JsonSerializable(typeof(RemoteOutboundProxySetRequest))]
[JsonSerializable(typeof(RemoteOutboundProxyProbeRequest))]
[JsonSerializable(typeof(RemoteOutboundProxyProbeCancelRequest))]
[JsonSerializable(typeof(RemoteOutboundProxyProbeResult))]
public partial class NetworkProtocolJsonContext : JsonSerializerContext;



/** Optional Notifications wire contract; method and capability values are stable. */
public static class NotificationsProtocol
{
    public const string Capability = "notify.history";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodNotifyList, MethodNotifyMarkRead]);

    public const string MethodNotifyList = "host.notify.list";
    public const string MethodNotifyMarkRead = "host.notify.markRead";
    public const string NotificationNotifyChanged = "host.notify.changed";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteNotifyQuery))]
[JsonSerializable(typeof(RemoteNotifyPage))]
[JsonSerializable(typeof(RemoteNotifyMarkRead))]
[JsonSerializable(typeof(RemoteNotificationChange))]
[JsonSerializable(typeof(NotificationRecord))]
[JsonSerializable(typeof(int))]
public partial class NotificationsProtocolJsonContext : JsonSerializerContext;



/** Optional Plugins wire contract; method and capability values are stable. */
public static class PluginsProtocol
{
    public const string Capability = "plugins.manage";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodPluginsList, MethodPluginPermission]);

    public const string MethodPluginsList = "host.plugins.list";
    public const string MethodPluginPermission = "host.plugins.permission";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemotePluginInfo))]
[JsonSerializable(typeof(IReadOnlyList<RemotePluginInfo>))]
[JsonSerializable(typeof(RemotePluginPermissionRequest))]
[JsonSerializable(typeof(string))]
public partial class PluginsProtocolJsonContext : JsonSerializerContext;



/** Optional Pty wire contract; method and capability values are stable. */
public static class PtyProtocol
{
    public const string Capability = "pty.daemon";
    public const string ResizeCapability = "pty.resize";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodPtyStart, MethodPtyWrite, MethodPtyStop, MethodPtyAttach, MethodPtyDetach, MethodPtyResize]);

    public const string MethodPtyStart = "host.pty.start";
    public const string MethodPtyWrite = "host.pty.write";
    public const string MethodPtyStop = "host.pty.stop";
    public const string MethodPtyAttach = "host.pty.attach";
    public const string MethodPtyDetach = "host.pty.detach";
    public const string MethodPtyResize = "host.pty.resize";
    public const string NotificationPtyOutput = "host.pty.output";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemotePtyStartRequest))]
[JsonSerializable(typeof(RemotePtyRef))]
[JsonSerializable(typeof(RemotePtyInput))]
[JsonSerializable(typeof(RemotePtyOutput))]
[JsonSerializable(typeof(RemotePtyResize))]
[JsonSerializable(typeof(RemotePtyAttachRequest))]
[JsonSerializable(typeof(RemotePtyFrame))]
[JsonSerializable(typeof(RemoteSubscriptionRef))]
[JsonSerializable(typeof(bool))]
public partial class PtyProtocolJsonContext : JsonSerializerContext;



/** Optional SessionEditing wire contract; method and capability values are stable. */
public static class SessionEditingProtocol
{
    public const string Capability = "session.editing";
    public static IReadOnlyList<string> Methods { get; } = Array.AsReadOnly<string>(
        [MethodSessionCommand, MethodSessionCommands, MethodSessionRename, MethodSessionDelete, MethodSessionApprovalPolicy, MethodSessionSubagents]);

    public const string MethodSessionCommand = "host.session.command";
    public const string MethodSessionCommands = "host.session.commands";
    public const string MethodSessionRename = "host.session.rename";
    public const string MethodSessionDelete = "host.session.delete";
    public const string MethodSessionApprovalPolicy = "host.session.approvalPolicy";
    public const string MethodSessionSubagents = "host.session.subagents";
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RemoteCommandRequest))]
[JsonSerializable(typeof(RemoteCommandResult))]
[JsonSerializable(typeof(RemoteCommandDescriptor))]
[JsonSerializable(typeof(IReadOnlyList<RemoteCommandDescriptor>))]
[JsonSerializable(typeof(RemoteSessionRenameRequest))]
[JsonSerializable(typeof(RemoteSessionPolicyRequest))]
[JsonSerializable(typeof(RemoteSubagentInfo))]
[JsonSerializable(typeof(IReadOnlyList<RemoteSubagentInfo>))]
[JsonSerializable(typeof(RemoteSessionRef))]
[JsonSerializable(typeof(bool))]
public partial class SessionEditingProtocolJsonContext : JsonSerializerContext;


/** Semantic names for all known wire capabilities; a host advertises only its installed domains. */
public static class HostProtocolCatalog
{
    public static IReadOnlyDictionary<string, string> KnownCapabilities { get; } =
        new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PendingBroadcast"] = HostProtocol.PendingBroadcastCapability,
            ["DecisionCancellation"] = DecisionProtocol.Capability,
            ["SessionRegistry"] = HostProtocol.SessionRegistryCapability,
            ["PtyDaemon"] = PtyProtocol.Capability,
            ["PtyResize"] = PtyProtocol.ResizeCapability,
            ["AgentSessions"] = AgentProtocol.Capability,
            ["GitInspect"] = GitProtocol.Capability,
            ["FsTransfer"] = FilesProtocol.Capability,
            ["NotifyHistory"] = NotificationsProtocol.Capability,
            ["SessionEditing"] = SessionEditingProtocol.Capability,
            ["PluginManagement"] = PluginsProtocol.Capability,
            ["NetworkOutbound"] = NetworkProtocol.Capability,
            ["NetworkProbeCancellation"] = NetworkProtocol.CancellableProbeCapability,
            ["NetworkReload"] = NetworkProtocol.ReloadCapability,
        });
}

