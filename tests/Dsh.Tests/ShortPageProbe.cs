using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 测试用的宿主后端探针: 把会话历史"永远少给最后一条"。
 *
 * 它模拟的是**补不齐**的宿主(分页被预算截断, 而后续请求又在同一位置被截断): 客户端必须照样把会话打开、
 * 说明缺哪一段、不写缓存、不无限重试。故意不实现 IRemoteLogSliceBackend —— 那样客户端才会走事件快照这条路。
 */
internal sealed class ShortPageProbe
{
    public int SnapshotRequests { get; private set; }

    public IRemoteHostBackend Decorate(IRemoteHostBackend inner) => new Backend(inner, this);

    private sealed class Backend(IRemoteHostBackend inner, ShortPageProbe probe) : IRemoteHostBackend
    {
        public HostPendingDecisions Pending => inner.Pending;
        public HostSessionRegistry Sessions => inner.Sessions;
        public IRemoteSessionBackend Session { get; } = new ShortSession(inner.Session, probe);
        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken) => inner.InfoAsync(cancellationToken);
        public IRemoteAgentBackend? Agent => inner.Agent;
        public IRemotePtyBackend? Pty => inner.Pty;
        public HostFileSystem? Files => inner.Files;
        public HostGitService? Git => inner.Git;
        public HostNotificationCenter? Notifications => inner.Notifications;
        public IRemoteSessionEditingBackend? SessionEditing => inner.SessionEditing;
        public IRemotePluginsBackend? Plugins => inner.Plugins;
        public IRemoteNetworkBackend? Network => inner.Network;
        public IRemoteEditorBackend? Editor => inner.Editor;
    }

    private sealed class ShortSession(IRemoteSessionBackend inner, ShortPageProbe probe) : IRemoteSessionBackend
    {
        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
            => inner.ListSessionsAsync(cancellationToken);

        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
            => inner.CreateSessionAsync(cwd, cancellationToken);

        public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
            => inner.ResumeSessionAsync(sessionId, cancellationToken);

        /** 每次都少给最后一条: 客户端补不齐, 但必须能打开并说清楚。 */
        public async Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(
            string sessionId, long fromEvent, int maxEvents, CancellationToken cancellationToken)
        {
            probe.SnapshotRequests += 1;
            var snapshot = await inner.GetSessionSnapshotAsync(sessionId, fromEvent, maxEvents, cancellationToken);
            var events = snapshot.Events;
            return events.Count <= 1 ? snapshot with { Events = [] } : snapshot with { Events = [.. events.Take(events.Count - 1)] };
        }

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken)
            => inner.SendMessageAsync(sessionId, text, images, cancellationToken);

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken)
            => inner.InterruptAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
            => inner.ListToolsAsync(sessionId, cancellationToken);

        public Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
            => inner.SubscribeAsync(sessionId, fromSeq, cancellationToken);
    }
}