namespace Dsh.RemoteHost;

/** 会话摘要(远端的 harness 会话)。 */
public sealed record RemoteSession(string Id, string Cwd, string Status, DateTimeOffset StartedAt);

/** 远端事件(会话事件流的一条)。 */
public sealed record RemoteEvent(string Kind, string? SessionId, string? Text, DateTimeOffset At);

/**
 * 远端工作区能力边界(§14): 本地显示、远端执行。
 * 一次性覆盖 会话 / 事件流 / 消息 / 工具 / 审批 / 文件 / 终端(PTY), 不留后续再补的尾巴(二次批注 #17)。
 * 本地实现直连进程内服务, 远程实现经 Dsh.Transport 代理; 两版 GUI 都通过这些成员使用。
 */
public interface IRemoteHost
{
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteSession>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<RemoteSession> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default);

    Task<RemoteSession> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<RemoteEvent> SubscribeAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SendMessageAsync(string sessionId, string text, CancellationToken cancellationToken = default);

    Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default);

    /** 审批/问答是「远端发起, 本地决策」: 用请求 id 回填决策。 */
    Task RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default);

    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    Task<string> StartPtyAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default);
}
