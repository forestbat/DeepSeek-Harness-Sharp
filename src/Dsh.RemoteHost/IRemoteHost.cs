namespace Dsh.RemoteHost;

/**
 * 远端工作区能力边界: 本地显示、远端执行。
 * 一次性覆盖 会话 / 事件流 / 消息 / 工具 / 审批 / 文件 / 终端(PTY), 不留后续再补的尾巴。
 * 本地实现直连进程内服务, 远程实现经 Dsh.Transport 代理(RemoteHostClient); 两版 GUI 都通过这些成员使用。
 */
public interface IRemoteHost
{
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<RemoteSessionInfo> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default);

    Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken = default);

    /** 停止某会话的事件推送(重新订阅前调用, 避免旧泵与新泵同时推送导致重复)。 */
    Task UnsubscribeAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default);

    Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default);

    /** 审批/问答是「远端发起, 本地决策」: 消费请求流, 用 RequestId 经 RespondApprovalAsync 回填。 */
    IAsyncEnumerable<RemoteApprovalRequest> ApprovalsAsync(CancellationToken cancellationToken = default);

    Task RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default);

    /** 问答请求流(远端发起); 本地决定后经 RespondQuestionAsync 回填。 */
    IAsyncEnumerable<RemoteQuestionRequest> QuestionsAsync(CancellationToken cancellationToken = default);

    Task RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default);

    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default);

    /** 列出远端某目录的子项(供“选择远端目录”浏览); Path 支持 `~` 前缀。 */
    Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);

    Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    Task<string> StartPtyAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default);

    Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default);

    /** 终止远端某个 PTY。 */
    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default);
}
