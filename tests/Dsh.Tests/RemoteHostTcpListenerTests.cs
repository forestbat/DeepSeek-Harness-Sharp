using System.Net;
using System.Net.Sockets;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 局域网 TCP 监听没有 run 目录里的 LocalEndpointToken 前导, 认证只在 JSON-RPC 握手里;
 * 非回环地址拒绝在没有 token 的情况下监听。
 */
public sealed class RemoteHostTcpListenerTests
{
    [Fact(Timeout = 30000)]
    public async Task TcpListener_ServesAuthenticatedClientAndRejectsWrongToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var backend = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(cancellationToken);
        var token = Guid.NewGuid().ToString("N");
        var server = new RemoteHostServer(new RemoteHostServerOptions(token), backend);
        var bound = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var serving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var servingToken = serving.Token;
        var listener = Task.Run(
            () => RemoteHostListener.ServeTcpAsync(server, new IPEndPoint(IPAddress.Loopback, 0), servingToken, endpoint =>
            {
                bound.TrySetResult(endpoint);
                return Task.CompletedTask;
            }),
            cancellationToken);
        var endpoint = await bound.Task.WaitAsync(cancellationToken);

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(endpoint, cancellationToken);
            await using var stream = tcp.GetStream();
            var error = await Assert.ThrowsAsync<IOException>(() => RemoteHostClient.ConnectAsync(stream, $"{token}-wrong", "tcp-test", cancellationToken));
            Assert.Contains("handshake rejected", error.Message);
        }

        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(endpoint, cancellationToken);
            await using var stream = tcp.GetStream();
            await using var client = await RemoteHostClient.ConnectAsync(stream, token, "tcp-test", cancellationToken);
            Assert.Equal(HostProtocol.Version, (await client.InfoAsync(cancellationToken)).ProtocolVersion);
        }

        await serving.CancelAsync();
        try
        {
            await listener;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact(Timeout = 30000)]
    public async Task NonLoopbackListener_WithoutToken_IsRefused()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var backend = await RemoteHostCapabilityTests.FakeBackend.CreateAsync(cancellationToken);
        var server = new RemoteHostServer(new RemoteHostServerOptions(), backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RemoteHostListener.ServeTcpAsync(server, new IPEndPoint(IPAddress.Any, 0), cancellationToken));
    }
}
