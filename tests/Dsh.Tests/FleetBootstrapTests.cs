using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dsh.Fleet;
using Dsh.RemoteHost;
using Dsh.Transport;

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
        await using var peer = new JsonRpcPeer(stream);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Closed += _ => closed.TrySetResult();
        peer.RequestHandler = async (message, requestCancellation) =>
        {
            string[] capabilities = [HostProtocol.PendingBroadcastCapability, HostProtocol.SessionRegistryCapability];
            if (message.Method == HostProtocol.MethodHello)
                return JsonSerializer.SerializeToElement(new HostHelloResponse(true, HostProtocol.Version, HostProtocol.ReleaseVersion, null, capabilities), HostProtocolJsonContext.Default.HostHelloResponse);
            if (message.Method == HostProtocol.MethodInfo)
                return JsonSerializer.SerializeToElement(new HostInfo(HostProtocol.Version, HostProtocol.ReleaseVersion, "linux", root, capabilities), HostProtocolJsonContext.Default.HostInfo);
            if (message.Method == HostProtocol.MethodPing)
            {
                await Task.Delay(Timeout.Infinite, requestCancellation);
                return null;
            }
            throw new JsonRpcException(JsonRpcPeer.MethodNotFound, "The fault endpoint only provides metadata.");
        };
        peer.Start();
        try { await closed.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
}

