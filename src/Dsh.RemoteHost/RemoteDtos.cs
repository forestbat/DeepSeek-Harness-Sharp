namespace Dsh.RemoteHost;

/** 会话摘要: Cwd/Status/时间供侧栏分组与排序, 标题与摘要供列表显示。 */
public sealed record RemoteSessionInfo(string Id, string Cwd, string Status, long StartedAt, string? Title, string? Summary, long UpdatedAt);

/** 会话事件流的一条: EventJson 是该 SessionEvent 的完整序列化(含 type/seq/time/data/surfaceOp), 本地据此重建会话。 */
public sealed record RemoteEventInfo(string SessionId, string EventJson);

/** 订阅会话事件: FromSeq 之前的作为历史先补发, 之后的实时推送(local 重连后可见增量)。 */
public sealed record RemoteSubscribeRequest(string SessionId, long FromSeq);

/** 远端发起的审批/问答: 需本地决策, 用 RequestId 回填。 */
public sealed record RemoteApprovalRequest(string RequestId, string SessionId, string Tool, string? Arguments, string? Reason);

/** 远端发起的问答: 问题项与本地 ask_user_question 同构。 */
public sealed record RemoteQuestionItem(
    string Id,
    string Question,
    string? Detail,
    string? Header,
    IReadOnlyList<RemoteQuestionOption> Options,
    bool MultiSelect,
    string? ApproveLabel);

public sealed record RemoteQuestionOption(string Label, string? Description);

public sealed record RemoteQuestionRequest(string RequestId, string SessionId, IReadOnlyList<RemoteQuestionItem> Questions);

public sealed record RemoteQuestionAnswerItem(string Id, IReadOnlyList<string> Selected, string? Custom);

public sealed record RemoteQuestionResponse(string RequestId, IReadOnlyList<RemoteQuestionAnswerItem> Answers);

public sealed record RemoteSessionCreateRequest(string Cwd);

public sealed record RemoteSessionRef(string SessionId);

public sealed record RemoteMessageRequest(string SessionId, string Text, IReadOnlyList<RemoteImageBlock>? Images = null);

/** 远端消息里的一张图片: 字节经 base64 传输, 由远端落进附件库后作为 ImageBlock 发给会话。 */
public sealed record RemoteImageBlock(string MediaType, int Width, int Height, string? Name, string Base64);

public sealed record RemoteApprovalResponse(string RequestId, bool Allow, string? Reason);

public sealed record RemoteFilePath(string Path);

/** 远端文件系统中的一项: 名称 + 绝对路径 + 是否目录。 */
public sealed record RemoteDirectoryEntry(string Name, string Path, bool IsDirectory);

/** 一次远端目录列举: 被列举目录的绝对路径 + 父目录(根为 null) + 子项。 */
public sealed record RemoteDirectoryListing(string Path, string? Parent, IReadOnlyList<RemoteDirectoryEntry> Entries);

public sealed record RemoteFileContent(string Path, string Base64);

public sealed record RemotePtyStartRequest(string FileName, IReadOnlyList<string> Arguments);

public sealed record RemotePtyRef(string PtyId);

public sealed record RemotePtyInput(string PtyId, string Base64);

public sealed record RemotePtyOutput(string PtyId, string Base64, bool Eof);
