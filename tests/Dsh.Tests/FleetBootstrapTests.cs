using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.RemoteHost.Rpc;
using Dsh.Transport;
using StreamJsonRpc;

namespace Dsh.Tests;

public sealed class FleetBootstrapTests
{
    [Theory(Timeout = 5000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SilentPeerBeforeInitialSnapshots_SettlesDisconnectedAfterHeartbeatTimeout(bool reconnect)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The isolated fault endpoint uses a Unix socket.");
            return;
        }
        var root = TempTree.CreateSocketDirectory("fb");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var token = LocalEndpointToken.Issue(RemoteHostEndpoint.EndpointFile(root));
        using var listener = OwnerOnlyFiles.ListenUnixSocket(RemoteHostEndpoint.SocketPath(root), 1);
        var serving = ServeMetadataThenGoSilentAsync(listener, token, root, lifetime.Token);
        try
        {
            await using var connection = new MachineConnection(MachineSpec.Local(root), new FleetOptions
            {
                AutoReconnect = reconnect,
                InitialBackoff = TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromSeconds(1),
                HeartbeatInterval = TimeSpan.FromMilliseconds(50),
                HeartbeatTimeout = TimeSpan.FromMilliseconds(250),
            });
            connection.Start();
            await connection.WaitSettledAsync(lifetime.Token).WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
            Assert.Equal(MachineConnectionState.Offline, connection.State);
            Assert.NotNull(connection.Info);
            if (reconnect)
            {
                using var retry = await listener.AcceptAsync(lifetime.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), lifetime.Token);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await serving;
            TempTree.Delete(root);
        }
    }

    /** A real authenticated RPC connection with valid metadata deliberately withholds every initial projection and ping reply. */
    private static async Task ServeMetadataThenGoSilentAsync(Socket listener, LocalEndpointToken token, string root,
        CancellationToken cancellationToken)
    {
        using var socket = await listener.AcceptAsync(cancellationToken);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        var preamble = new byte[LocalEndpointToken.ValueLength];
        await stream.ReadExactlyAsync(preamble, cancellationToken);
        Assert.True(token.Matches(Encoding.UTF8.GetString(preamble)));
        using var peer = JsonRpcChannel.Serve(stream, HostRpcFormatter.CreateJson(), new JsonRpcTarget(RpcTargetMetadata.FromShape<IHostRpc>(), new MetadataOnlyEndpoint(root)));
        try { await peer.Completion.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /** 只回握手与元数据, 其余方法一律不存在, 且 ping 永不回。 */
    private sealed class MetadataOnlyEndpoint(string root) : IHostRpc
    {
        private static IReadOnlyList<string> Capabilities { get; } =
            [HostProtocol.PendingBroadcastCapability, HostProtocol.SessionRegistryCapability];

        public Task<HostHelloResponse> HelloAsync(HostHelloRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new HostHelloResponse(true, HostProtocol.Version, HostProtocol.ReleaseVersion, null, Capabilities));

        public Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new HostInfo(HostProtocol.Version, HostProtocol.ReleaseVersion, "linux", root, Capabilities));

        public Task<long> PingAsync(CancellationToken cancellationToken = default)
            => throw new LocalRpcException("The fault endpoint never answers ping.") { ErrorCode = JsonRpcException.MethodNotFound };

        public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default) => throw Missing();
        public Task<RemoteSessionSummary> CreateSessionAsync(RemoteSessionCreateRequest request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<RemoteSessionSummary> ResumeSessionAsync(RemoteSessionRef request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<RemoteSubscriptionRef> SubscribeAsync(RemoteSubscribeRequest request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<bool> UnsubscribeAsync(RemoteSubscriptionRef request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<bool> SendMessageAsync(RemoteMessageRequest request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<bool> InterruptAsync(RemoteSessionRef request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(RemoteSessionSnapshotRequest request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<RemoteLogSlice> GetSessionLogSliceAsync(RemoteLogSliceRequest request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<IReadOnlyList<string>> ListToolsAsync(RemoteSessionRef request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<IReadOnlyList<RemoteSessionSummary>> ListRegistryAsync(CancellationToken cancellationToken = default) => throw Missing();
        public Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default) => throw Missing();
        public Task<bool> RespondApprovalAsync(RemoteApprovalResponse request, CancellationToken cancellationToken = default) => throw Missing();
        public Task<bool> RespondQuestionAsync(RemoteQuestionResponse request, CancellationToken cancellationToken = default) => throw Missing();

        private static LocalRpcException Missing()
            => new("The fault endpoint only provides metadata.") { ErrorCode = JsonRpcException.MethodNotFound };
    }
}

