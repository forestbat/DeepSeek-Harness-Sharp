namespace Dsh.RemoteHost;

/** 会话摘要。 */
public sealed record RemoteSessionInfo(string Id, string Cwd, string Status, long StartedAt);

/** 会话事件流的一条(Kind 见 RemoteEventKind)。 */
public sealed record RemoteEventInfo(string SessionId, string Kind, string? Text, long At);

/** 远端发起的审批/问答: 需本地决策, 用 RequestId 回填。 */
public sealed record RemoteApprovalRequest(string RequestId, string SessionId, string Prompt, string? Tool);

public sealed record RemoteSessionCreateRequest(string Cwd);

public sealed record RemoteSessionRef(string SessionId);

public sealed record RemoteMessageRequest(string SessionId, string Text);

public sealed record RemoteApprovalResponse(string RequestId, bool Allow, string? Reason);

public sealed record RemoteFilePath(string Path);

public sealed record RemoteFileContent(string Path, string Base64);

public sealed record RemotePtyStartRequest(string FileName, IReadOnlyList<string> Arguments);

public sealed record RemotePtyRef(string PtyId);

public sealed record RemotePtyInput(string PtyId, string Base64);

public sealed record RemotePtyOutput(string PtyId, string Base64, bool Eof);
