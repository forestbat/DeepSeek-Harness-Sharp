using Dsh.Pty;

namespace Dsh.Tui;

/** 常驻 TUI 访问 daemon pty 的入口: 便于测试替换为假实现, 不依赖真实 daemon 套接字/命名管道。 */
internal interface IDaemonPtyAccess
{
    Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync();

    Task ControlSendAsync(string ptyId, string kind, int paneId, string payload);
}

/** 默认实现: 直连 daemon。 */
internal sealed class RealDaemonPtyAccess : IDaemonPtyAccess
{
    public Task<IReadOnlyList<PtyDaemonSessionDto>> ListAsync()
        => PtyDaemonClient.ListAsync();

    public Task ControlSendAsync(string ptyId, string kind, int paneId, string payload)
        => PtyDaemonClient.ControlSendAsync(ptyId, kind, paneId, payload);
}
