using Dsh.Core;
using Dsh.GitInspect;
using Dsh.Llm;
using Dsh.Notifications;
using Dsh.Transport;


namespace Dsh.RemoteHost;


/** One host stream item: the authoritative session event, or a reset marker before a full replay. */
public sealed record RemoteSessionEvent(string SessionId, SessionEvent? Event = null, bool Reset = false);

/** 每个读者有独立订阅标识，历史和实时事件均由 Host 提供。 */
public sealed record RemoteSubscribeRequest(string SessionId, long FromSeq, string SubscriptionId);

public sealed record RemoteSubscriptionRef(string SubscriptionId);

public readonly record struct RemoteEventFrame(string SubscriptionId, RemoteSessionEvent Event);


/** 会话历史分页请求: 从 FromEvent 起返回装得进 MaxBytes 的一段; 响应里的 TotalEventCount 让客户端接着取下一页。 */
public sealed record RemoteSessionSnapshotRequest(string SessionId, long FromEvent = 0, int MaxBytes = HostProtocol.SnapshotPageBytes);

/** 会话历史的一页; TotalEventCount 是整段历史的条数, 客户端据此取完剩余各页。 */
public sealed record RemoteSessionSnapshot(
    SessionHeader Header,
    IReadOnlyList<SessionEvent> Events,
    long TotalEventCount,
    string? ApprovalPolicy = null);


/** 远端发起的审批/问答: 需本地决策, 用 RequestId 回填。 */
public sealed record RemoteApprovalRequest(string RequestId, string SessionId, string Tool, string? Arguments, string? Reason, string? Impact = null, RemoteApprovalAvailability? Availability = null);

/** Per-request choices that the provider can represent without changing the user's requested scope. */
public readonly record struct RemoteApprovalAvailability(bool AllowOnce, bool AllowForSession, bool Reject);

public sealed record RemoteQuestionOption(string Label, string? Description, string? Value = null, bool Disabled = false, bool Default = false);

public sealed record RemoteQuestionItem(
    string Id,
    string Question,
    string? Detail,
    string? Header,
    IReadOnlyList<RemoteQuestionOption> Options,
    bool MultiSelect,
    string? ApproveLabel,
    bool AllowCustom = true);

public sealed record RemoteQuestionRequest(string RequestId, string SessionId, IReadOnlyList<RemoteQuestionItem> Questions);

public sealed record RemoteQuestionAnswerItem(string Id, IReadOnlyList<string> Selected, string? Custom);

public sealed record RemoteQuestionResponse(string RequestId, IReadOnlyList<RemoteQuestionAnswerItem> Answers, string Status = "answered", string? Reason = null);

public sealed record RemoteSessionCreateRequest(string Cwd);

public sealed record RemoteSessionRef(string SessionId);

public sealed record RemoteMessageRequest(string SessionId, string Text, IReadOnlyList<RemoteImageBlock>? Images = null);

/** 远端消息里的一张图片: 字节经 base64 传输, 由远端落进附件库后作为 ImageBlock 发给会话。 */
public sealed record RemoteImageBlock(string MediaType, int Width, int Height, string? Name, string Base64);

public sealed record RemoteApprovalResponse(string RequestId, bool Allow, string? Reason, bool RememberForSession = false, bool Cancelled = false);


/** 全部未决的审批与问答(host.pending.list), 也是新连接补发的内容。 */
public sealed record RemotePendingList(IReadOnlyList<RemoteApprovalRequest> Approvals, IReadOnlyList<RemoteQuestionRequest> Questions);

/** 完整权威待决投影; Resolution 仅携带本次结局的审计信息。 */
public sealed record RemotePendingChange(RemotePendingList Pending, RemotePendingResolved? Resolution = null);

/** 未决请求的结局: Kind 见 PendingKinds, Resolution 见 PendingResolutions; 由 host 取消时 ResolvedBy 为 "host"、ResolvedByConnection 为空。 */
public sealed record RemotePendingResolved(
    string RequestId,
    string Kind,
    string SessionId,
    string Resolution,
    string ResolvedBy,
    string? ResolvedByConnection);

public static class PendingKinds
{
    public const string Approval = "approval";
    public const string Question = "question";
}

public static class PendingResolutions
{
    public const string Allowed = "allowed";
    public const string Rejected = "rejected";
    public const string Answered = "answered";
    public const string Dismissed = "dismissed";

    /** 会话被中断、结束或 host 退出, 请求不再有人等待。 */
    public const string Cancelled = "cancelled";
}

/** Registry stream starts with a complete snapshot, followed by per-session changes. */
public sealed record RemoteSessionChange(IReadOnlyList<RemoteSessionSummary>? Snapshot = null, RemoteSessionSummary? Session = null);

public static class HostSessionKinds
{
    public const string Harness = "harness";
    public const string External = "external";
    public const string Acp = "acp";
    public const string Terminal = "terminal";
}

/** agentKind 取值: 会话里跑的是哪种 Agent; 外部 Agent 的取值由各自的适配器登记。 */
public static class HostAgentKinds
{
    /** 本项目自己的 Agent(harness 会话)。 */
    public const string Dsharp = "dsharp";
}

/** 只反映进程与交互状态, 不判断任务是否完成。 */
public static class HostSessionStatuses
{
    public const string Running = "Running";
    public const string WaitingForUser = "WaitingForUser";
    public const string Idle = "Idle";
    public const string Exited = "Exited";
}


/** How an external agent runs: its full-screen TUI inside a PTY, or its ACP channel over stdio. */
public static class RemoteAgentModes
{
    public const string Terminal = "terminal";
    public const string Acp = "acp";
}

/**
 * One startable agent kind on this host (host.agent.catalog).
 * AgentKind is what host.agent.start takes; SessionKind is the kind the started session is registered under in the default mode.
 * RequiresCommand marks the user-defined kinds (raw terminal, generic ACP) that need RemoteAgentStartRequest.Command.
 */
public sealed record RemoteAgentCatalogEntry(
    string AgentKind,
    string DisplayName,
    string SessionKind,
    bool Available,
    bool SupportsTerminal,
    bool SupportsAcp,
    bool SupportsResume,
    bool RequiresCommand,
    string? InteractivePath = null,
    string? AcpPath = null,
    string? Version = null);

/** Executable plus arguments for a user-defined agent; an argument equal to "{prompt}" is replaced by the initial prompt. */
public sealed record RemoteAgentCommand(string Executable, IReadOnlyList<string> Arguments);

/**
 * Optional git worktree for a new session: an existing worktree of the project's repository at Path is reused, otherwise one is added there.
 * NewBranch and StartPoint follow `git worktree add -b <NewBranch> <Path> <StartPoint>`.
 */
public sealed record RemoteWorktreeChoice(string Path, string? NewBranch = null, string? StartPoint = null);

/**
 * host.agent.start: one entry point for every session kind.
 * AgentKind "dsharp" starts a harness session (Selection/Provider as in host.session.create); built-in adapter kinds start that CLI;
 * "terminal" and "acp" run Command. Mode defaults to acp for "acp" and terminal otherwise.
 * Environment is added to the agent process only; terminal status reflects process lifecycle only.
 */
public sealed record RemoteAgentStartRequest(
    string AgentKind,
    string ProjectPath,
    string? InitialPrompt = null,
    string? Mode = null,
    RemoteAgentCommand? Command = null,
    RemoteWorktreeChoice? Worktree = null,
    string? Title = null,
    int? Columns = null,
    int? Rows = null,
    IReadOnlyDictionary<string, string>? Environment = null);

/** host.agent.prompt: one user turn for an ACP or harness session. */
public sealed record RemoteAgentPrompt(string SessionId, string Text);


/** host.fs.stat: Exists is false (and the rest empty) for a missing path; times are Unix milliseconds. */
public sealed record RemoteFileStat(
    string Path,
    bool Exists,
    bool IsDirectory = false,
    bool IsSymbolicLink = false,
    long Size = 0,
    long ModifiedAt = 0);

public sealed record RemoteFsRename(string From, string To, bool Overwrite = false);

/** Deleting a non-empty directory needs Recursive. */
public sealed record RemoteFsDelete(string Path, bool Recursive = false);

/** host.fs.upload.begin: bytes go to a temporary file next to Path and replace Path atomically on commit. */
public sealed record RemoteUploadBegin(string Path, bool Overwrite = true);

/** ChunkBytes is the largest chunk the host accepts. */
public sealed record RemoteUploadRef(string UploadId, int ChunkBytes = 0);

/** Offset must equal the number of bytes received so far, so a retried chunk is rejected instead of written twice. */
public sealed record RemoteUploadChunk(string UploadId, long Offset, string Base64);

/** Size, when given, must match the received byte count. */
public sealed record RemoteUploadCommit(string UploadId, long? Size = null);

/** host.fs.download: Length is capped at the host's chunk size and defaults to it. */
public sealed record RemoteDownloadRequest(string Path, long Offset, int? Length = null);

public sealed record RemoteFileChunk(string Path, long Offset, string Base64, long TotalSize, bool Eof);

public sealed record RemoteFilePath(string Path);

/** A file or directory entry; timestamps are Unix milliseconds. */
public sealed record RemoteDirectoryEntry(string Name, string Path, bool IsDirectory, long? Size = null, long? ModifiedAt = null);

/** One directory listing, including its parent when present. */
public sealed record RemoteDirectoryListing(string Path, string? Parent, IReadOnlyList<RemoteDirectoryEntry> Entries);

public sealed record RemoteFileContent(string Path, string Base64);



/** host.git.status / host.git.worktree.list: ProjectPath is any directory inside the repository. */
public sealed record RemoteGitRequest(string ProjectPath);

public sealed record RemoteGitDiffRequest(
    string ProjectPath,
    GitDiffTarget Target,
    IReadOnlyList<string>? Paths = null,
    GitDiffLimits? Limits = null);

public sealed record RemoteGitLogRequest(string ProjectPath, int? MaxCount = null, string? Revision = null);

public sealed record RemoteGitWorktreeAdd(string ProjectPath, string Path, string? NewBranch = null, string? StartPoint = null);

public sealed record RemoteGitWorktreeRemove(string ProjectPath, string Path, bool Force = false);


public sealed record RemoteOutboundProxySettings(string? Url = null, IReadOnlyList<string>? Bypass = null, string? ProbeUrl = null);

public sealed record RemoteOutboundProxyState(string OwnerId, string HostHome, string Revision, RemoteOutboundProxySettings Settings);

public sealed record RemoteOutboundProxySetRequest(string ExpectedRevision, RemoteOutboundProxySettings Settings);

public sealed record RemoteOutboundProxyProbeRequest(RemoteOutboundProxySettings? Draft = null, string? ProbeUrl = null, Guid? OperationId = null);

public sealed record RemoteOutboundProxyProbeCancelRequest(Guid OperationId);

public readonly record struct RemoteOutboundProxyProbeResult(bool Reachable, string Message, string? Stage, int? HttpStatus, string? HostHome);



/** host.notify.list: newest first; Kinds empty or null matches every kind. */
public sealed record RemoteNotifyQuery(
    string? SessionId = null,
    bool UnreadOnly = false,
    IReadOnlyList<NotificationKind>? Kinds = null,
    int Offset = 0,
    int? Limit = null);

/** Total counts the matching notifications before paging; UnreadCount is over the whole history. */
public sealed record RemoteNotifyPage(IReadOnlyList<NotificationRecord> Items, int Total, int UnreadCount);

/** host.notify.markRead: All marks every unread notification (of SessionId when given); otherwise Ids. */
public sealed record RemoteNotifyMarkRead(IReadOnlyList<string>? Ids = null, bool All = false, string? SessionId = null);

/** Authoritative notification projection with optional audit information for the triggering change. */
public sealed record RemoteNotificationChange(RemoteNotifyPage Page, NotificationRecord? Added = null, IReadOnlyList<string>? ReadIds = null);


public sealed record RemotePluginInfo(string Package, string Description, bool Enabled, bool ReadAllowed, bool WriteAllowed, bool NetworkAllowed);

public sealed record RemotePluginPermissionRequest(string Package, string Permission, bool Allowed);


public sealed record RemotePtyAttachRequest(string PtyId, string SubscriptionId);

public readonly record struct RemotePtyFrame(string SubscriptionId, RemotePtyOutput Output);

/** 起 PTY: Cwd/Title 为空时取远端工作区目录与命令行; 尺寸为空时用 daemon 默认值。 */
public sealed record RemotePtyStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? Cwd = null,
    int? Columns = null,
    int? Rows = null,
    string? Title = null,
    IReadOnlyDictionary<string, string>? Environment = null);

public sealed record RemotePtyResize(string PtyId, int Columns, int Rows);

public sealed record RemotePtyRef(string PtyId);

public sealed record RemotePtyInput(string PtyId, string Base64);

public sealed record RemotePtyOutput(string PtyId, string Base64, bool Eof);


public sealed record RemoteCommandRequest(string SessionId, string Line);

public sealed record RemoteCommandResult(bool Known, string? Text, string? FollowupPrompt, bool Success = false);

public sealed record RemoteCommandDescriptor(string Name, string Description);

public sealed record RemoteSessionRenameRequest(string SessionId, string Title);

public sealed record RemoteSessionPolicyRequest(string SessionId, string Policy);

public sealed record RemoteSubagentInfo(string SessionId, string? Parent, int Depth, string Mode, string? Label, bool HasChildren, bool IsLive, long CreatedAt);



/** The single host-side session summary: authoritative header plus execution/registry state. */
public sealed record RemoteSessionSummary
{
    [System.Text.Json.Serialization.JsonConstructor]
    public RemoteSessionSummary()
    {
    }

    public SessionHeader Header { get; init; } = null!;
    public string Status { get; init; } = "";
    public long UpdatedAt { get; init; }
    public long LastActivityAt { get; init; }
    public string? Kind { get; init; }
    public string? AgentKind { get; init; }
    public int PendingCount { get; init; }
    public int? ExitCode { get; init; }
    public bool Deleted { get; init; }
    public string? Summary { get; init; }

    public string Id => Header.Id.Value;
    public string SessionId => Header.Id.Value;
    public string Cwd => Header.Cwd ?? "";
    public string? ProjectPath => Header.Cwd;
    public string? Title => Header.Title;
    public long StartedAt => Header.CreatedAt;
    public long CreatedAt => Header.CreatedAt;

    public RemoteSessionSummary(
        SessionHeader header,
        string status,
        long updatedAt,
        long lastActivityAt,
        string? kind = null,
        string? agentKind = null,
        int pendingCount = 0,
        int? exitCode = null,
        bool deleted = false,
        string? summary = null)
    {
        Header = header;
        Status = status;
        UpdatedAt = updatedAt;
        LastActivityAt = lastActivityAt;
        Kind = kind;
        AgentKind = agentKind;
        PendingCount = pendingCount;
        ExitCode = exitCode;
        Deleted = deleted;
        Summary = summary;
    }

    /** Compatibility shape for the former RemoteSessionSummary. */
    public RemoteSessionSummary(string id, string cwd, string status, long startedAt, string? title, string? summary, long updatedAt)
        : this(
            new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = new SessionId(id),
                CreatedAt = startedAt,
                Cwd = cwd,
                Title = title,
                IsSeeded = false,
            },
            status,
            updatedAt,
            updatedAt,
            null,
            null,
            0,
            null,
            false,
            summary)
    {
    }

    /** Compatibility shape for the former RemoteSessionSummary registry entry. */
    public RemoteSessionSummary(
        string sessionId,
        string kind,
        string? agentKind,
        string? projectPath,
        string? title,
        string status,
        long createdAt,
        long lastActivityAt,
        int pendingCount,
        int? exitCode,
        bool deleted = false)
        : this(
            new SessionHeader
            {
                Version = SessionHeader.SessionFormatVersion,
                Id = new SessionId(sessionId),
                CreatedAt = createdAt,
                Cwd = projectPath,
                Title = title,
                IsSeeded = false,
            },
            status,
            lastActivityAt,
            lastActivityAt,
            kind,
            agentKind,
            pendingCount,
            exitCode,
            deleted)
    {
    }
}


/**
 * 远端宿主的端点约定(与 Pty daemon 同款: DSH_HOME/run 下的 Unix socket / Windows 端口文件)。
 * stdio-over-SSH 不需要端点; loopback socket 供端口转发或本机连接使用(§14 §4)。
 */
public static class RemoteHostEndpoint
{
    /** 与 PTY daemon 同一个根(含 .storage-root 重定向), 两处共用 LocalEndpointRoot。 */
    public static string DefaultRoot() => LocalEndpointRoot.Resolve();

    public static string RunDirectory(string? root = null) => Path.Combine(root ?? DefaultRoot(), "run");

    public static string SocketPath(string? root = null) => Path.Combine(RunDirectory(root), "host.sock");

    public static string PortFile(string? root = null) => Path.Combine(RunDirectory(root), "host.port");

    /** The file that announces the endpoint on this platform (socket or port file); the daemon's LocalEndpointToken sits next to it. */
    public static string EndpointFile(string? root = null) => OperatingSystem.IsWindows() ? PortFile(root) : SocketPath(root);
}

/** An acquired backend reader. Dispose its owner even when cancellation prevents the first enumeration. */
public sealed class RemoteHostStream<T>(IAsyncEnumerable<T> stream, Func<ValueTask>? release = null) : IAsyncEnumerable<T>, IAsyncDisposable
{
    private Func<ValueTask>? _release = release;

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => stream.GetAsyncEnumerator(cancellationToken);

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
}

