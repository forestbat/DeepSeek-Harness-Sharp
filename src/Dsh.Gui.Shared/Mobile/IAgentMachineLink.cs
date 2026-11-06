using Dsh.RemoteHost;

namespace Dsh.Gui.Shared.Mobile;

/**
 * agent 眼里的一台远端机器: 能看它有哪些会话, 能新建会话, 能往里发一句话。
 * 手机上的实现是 SSH.NET 那条已连好的链路, 与界面共用同一个连接, 不额外拉第二条。
 */
public interface IAgentMachineLink
{
    string Name { get; }

    bool IsConnected { get; }

    string? Detail { get; }

    IReadOnlyList<RemoteSessionSummary> Sessions { get; }

    Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken);

    Task SendMessageAsync(string sessionId, string text, CancellationToken cancellationToken);
}
