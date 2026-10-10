namespace Dsh.RemoteHost;

/**
 * 远端宿主的能力后端: 远端进程内的真实实现(harness)或测试替身都实现它, 与传输完全分离。
 * 服务端把它暴露成 RPC; 客户端经 RemoteHostProxy 变成 IRemoteHost。
 */
public interface IRemoteHostBackend
{
    Task<HostInfo> InfoAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken);

    Task<RemoteSessionInfo> CreateSessionAsync(string cwd, CancellationToken cancellationToken);

    Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken);

    Task SendMessageAsync(string sessionId, string text, CancellationToken cancellationToken);

    Task InterruptAsync(string sessionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken);

    Task RespondApprovalAsync(string requestId, bool allow, string? reason, CancellationToken cancellationToken);

    Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken);

    /** 列出远端某目录的子项; Path 支持 `~` 前缀。 */
    Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken);

    Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken);

    Task<string> StartPtyAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);

    Task WritePtyAsync(string ptyId, byte[] data, CancellationToken cancellationToken);

    Task StopPtyAsync(string ptyId, CancellationToken cancellationToken);

    /** 会话事件流(服务端订阅后经通道推给客户端)。 */
    IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, CancellationToken cancellationToken);

    /** 审批请求流(远端发起, 阻塞到本地经 RespondApprovalAsync 回填)。 */
    IAsyncEnumerable<RemoteApprovalRequest> ApprovalsAsync(CancellationToken cancellationToken);

    /** PTY 输出流。 */
    IAsyncEnumerable<RemotePtyOutput> PtyOutputAsync(string ptyId, CancellationToken cancellationToken);
}
