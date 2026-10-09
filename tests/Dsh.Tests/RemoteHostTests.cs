using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

public sealed class RemoteHostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task HandshakeInfoAndPingRoundTrip()
    {
        await using var fixture = await HostFixture.CreateAsync(token: null, Ct);
        Assert.NotNull(fixture.Client);
        var info = await fixture.Client!.InfoAsync(Ct);
        Assert.Equal(HostProtocol.Version, info.ProtocolVersion);
        Assert.False(string.IsNullOrEmpty(info.HostVersion));
        Assert.True(await fixture.Client.PingAsync(Ct) > 0);
        Assert.True(fixture.Client.Hello.Ok);
    }

    [Fact]
    public async Task WrongTokenIsRejected()
    {
        var error = await Assert.ThrowsAsync<IOException>(async () =>
        {
            await using var failed = await HostFixture.CreateAsync(token: "secret", Ct, connectToken: "wrong");
        });
        Assert.Contains("token", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VersionMismatchIsRejected()
    {
        await using var fixture = await HostFixture.CreateAsync(token: null, Ct, connect: false);
        await using var peer = new JsonRpcPeer(fixture.ClientStream);
        peer.Start();
        var request = JsonSerializer.SerializeToElement(
            new HostHelloRequest(null, HostProtocol.Version + 999), HostProtocolJsonContext.Default.HostHelloRequest);
        var result = await peer.RequestAsync(HostProtocol.MethodHello, request, Ct);
        var hello = JsonSerializer.Deserialize(result!.Value, HostProtocolJsonContext.Default.HostHelloResponse)!;
        Assert.False(hello.Ok);
        Assert.Contains("version", hello.Mismatch!, StringComparison.OrdinalIgnoreCase);
    }

    /** 一对经 TCP loopback 相连的 host server / client。 */
    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly TcpClient _serverClient;
        private readonly TcpClient _client;
        private readonly Task _serve;

        private HostFixture(TcpClient serverClient, TcpClient client, Task serve, RemoteHostClient? hostClient, Stream clientStream)
        {
            _serverClient = serverClient;
            _client = client;
            _serve = serve;
            Client = hostClient;
            ClientStream = clientStream;
        }

        public RemoteHostClient? Client { get; }

        public Stream ClientStream { get; }

        public static async Task<HostFixture> CreateAsync(
            string? token,
            CancellationToken cancellationToken,
            string? connectToken = null,
            bool connect = true)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var acceptTask = listener.AcceptTcpClientAsync(cancellationToken);
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
            var serverClient = await acceptTask;
            listener.Stop();

            var server = new RemoteHostServer(new RemoteHostServerOptions(token));
            var clientStream = client.GetStream();
            var serve = Task.Run(() => server.ServeAsync(serverClient.GetStream(), cancellationToken), cancellationToken);
            var hostClient = connect
                ? await RemoteHostClient.ConnectAsync(clientStream, connectToken ?? token, cancellationToken)
                : null;
            return new HostFixture(serverClient, client, serve, hostClient, clientStream);
        }

        public async ValueTask DisposeAsync()
        {
            if (Client is not null)
                await Client.DisposeAsync();
            _serverClient.Dispose();
            _client.Dispose();
            try
            {
                await _serve.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException)
            {
            }
        }
    }
}
