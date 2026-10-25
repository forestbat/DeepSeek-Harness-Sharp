using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dsh.Host;
using Dsh.RemoteHost;

namespace Dsh.Tests;

[Collection(SerialProcessCollection.Name)]
public sealed class RemoteHostPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;


    [Theory]
    [InlineData("[")]
    [InlineData("null")]
    public async Task InvalidSessionRegistry_FailsWithoutReplacingTheStoredSessions(string contents)
    {
        var root = TempTree.CreateDirectory("registry");
        try
        {
            var storePath = Path.Combine(root, "sessions.json");
            await File.WriteAllTextAsync(storePath, contents, Ct);

            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await using var registry = await HostSessionRegistry.OpenAsync(storePath, new HostPendingDecisions(), Ct);
            });

            Assert.Equal(contents, await File.ReadAllTextAsync(storePath, Ct));
            Assert.False(File.Exists($"{storePath}.tmp"));
        }
        finally
        {
            TempTree.Delete(root);
        }
    }

    /** 常驻 daemon + stdio 桥: 客户端断开后再接回来, 会话仍在(脱钩/重连)。 */
    [Fact(Explicit = true)]
    public async Task SessionsSurviveClientReconnectThroughBridge()
    {
        // 套接字路径有 108 字符上限: 用仓库内浅目录, 否则 <root>/run/host.sock 超长, daemon 绑不上。
        var root = TempTree.CreateSocketDirectory("rh");
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(root));
        await using var backend = await SessionBackend.CreateAsync(root, Ct);
        var server = new RemoteHostServer(new RemoteHostServerOptions(null), backend);
        using var daemonCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var daemon = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, root, daemonCts.Token), Ct);
        try
        {
            await using (var first = await ConnectViaBridgeAsync(root, Ct))
            {
                var created = await first.Client.CreateSessionAsync("/work", cancellationToken: Ct);
                Assert.Equal("/work", created.Cwd);
            }

            // 第一次的客户端/桥都已断开, daemon 仍在; 重新连入应看到同一会话。
            await using var second = await ConnectViaBridgeAsync(root, Ct);
            var sessions = await second.Client.ListSessionsAsync(Ct);
            Assert.Contains(sessions, session => session.Cwd == "/work");
        }
        finally
        {
            await daemonCts.CancelAsync();
            try
            {
                await daemon;
            }
            catch (OperationCanceledException)
            {
            }

            TempTree.Delete(root);
        }
    }

    /** 半开/卡住的连接不能阻塞后续连接: 每条连接必须独立服务。 */
    [Fact(Explicit = true)]
    public async Task StuckConnectionDoesNotBlockNewConnections()
    {
        // 套接字路径有 108 字符上限: 用仓库内浅目录, 否则 <root>/run/host.sock 超长, daemon 绑不上。
        var root = TempTree.CreateSocketDirectory("rh");
        TempTree.AssertFitsMacOsSocketLimit(RemoteHostEndpoint.SocketPath(root));
        await using var backend = await SessionBackend.CreateAsync(root, Ct);
        var server = new RemoteHostServer(new RemoteHostServerOptions(null), backend);
        using var daemonCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var daemon = Task.Run(() => RemoteHostListener.ServeLoopbackAsync(server, root, daemonCts.Token), Ct);
        try
        {
            // 第一条连接建立后不发任何数据(半开): 旧实现串行服务, 会让 daemon 卡在这条连接上。
            await using var stuck = await RemoteHostConnector.ConnectAsync(root, Ct);

            // 第二条连接应能正常完成握手与请求。
            await using var stream = await RemoteHostConnector.ConnectAsync(root, Ct);
            await using var client = await RemoteHostClient.ConnectAsync(stream, null, Ct);
            var info = await client.InfoAsync(Ct);
            Assert.Equal(HostProtocol.Version, info.ProtocolVersion);
        }
        finally
        {
            await daemonCts.CancelAsync();
            try
            {
                await daemon;
            }
            catch (OperationCanceledException)
            {
            }

            TempTree.Delete(root);
        }
    }

    /** 模拟 stdio-over-SSH: 一端是“ssh 客户端”, 另一端交给桥转发到 daemon。 */
    private static async Task<BridgeConnection> ConnectViaBridgeAsync(string root, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        var bridge = await acceptTask;
        listener.Stop();

        var bridgeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var bridgeTask = Task.Run(async () => await HostBridge.RunAsync(bridge.GetStream(), root, bridgeCts.Token), cancellationToken);
        var hostClient = await RemoteHostClient.ConnectAsync(client.GetStream(), null, cancellationToken);
        return new BridgeConnection(hostClient, client, bridge, bridgeTask, bridgeCts);
    }

    private sealed class BridgeConnection(
        RemoteHostClient client,
        TcpClient clientTcp,
        TcpClient bridgeTcp,
        Task bridgeTask,
        CancellationTokenSource bridgeCts) : IAsyncDisposable
    {
        public RemoteHostClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            clientTcp.Dispose();
            bridgeTcp.Dispose();
            await bridgeCts.CancelAsync();
            try
            {
                await bridgeTask;
            }
            catch (OperationCanceledException)
            {
            }

            bridgeCts.Dispose();
        }
    }

    /** Reconnect fixture supplies only core sessions; optional methods must not become empty services. */
    private sealed class SessionBackend : IRemoteHostBackend, IRemoteSessionBackend, IAsyncDisposable
    {
        private readonly Dictionary<string, RemoteSessionSummary> _sessions = [];

        private SessionBackend(HostPendingDecisions pending, HostSessionRegistry sessions)
        {
            Pending = pending;
            Sessions = sessions;
        }

        public static async Task<SessionBackend> CreateAsync(string root, CancellationToken cancellationToken)
        {
            var pending = new HostPendingDecisions();
            var sessions = await HostSessionRegistry.OpenAsync(Path.Combine(root, "sessions.json"), pending, cancellationToken);
            return new SessionBackend(pending, sessions);
        }

        public HostPendingDecisions Pending { get; }

        public HostSessionRegistry Sessions { get; }

        public IRemoteSessionBackend Session => this;
        public IRemoteAgentBackend? Agent => null;
        public IRemotePtyBackend? Pty => null;
        public HostFileSystem? Files => null;
        public HostGitService? Git => null;
        public HostNotificationCenter? Notifications => null;
        public IRemoteSessionEditingBackend? SessionEditing => null;
        public IRemotePluginsBackend? Plugins => null;
        public IRemoteNetworkBackend? Network => null;

        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => Task.FromResult(new HostInfo(HostProtocol.Version, "test", "test", "/home"));

        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RemoteSessionSummary>>([.. _sessions.Values]);

        public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken)
        {
            var session = new RemoteSessionSummary($"session-{_sessions.Count + 1}", cwd, "Idle", 0, null, null, 0);
            _sessions[session.Id] = session;
            return Task.FromResult(session);
        }

        public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : throw new KeyNotFoundException(sessionId));

        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<RemoteHostStream<RemoteSessionEvent>> SubscribeAsync(string sessionId, long fromSeq, CancellationToken cancellationToken)
            => Task.FromResult(new RemoteHostStream<RemoteSessionEvent>(AsyncEnumerable.Empty<RemoteSessionEvent>()));

        public async ValueTask DisposeAsync()
        {
            Pending.Dispose();
            await Sessions.DisposeAsync();
        }
    }
}






