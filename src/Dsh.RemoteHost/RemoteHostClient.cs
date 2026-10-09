using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** 本地 GUI/CLI 侧连接远端 host: 先握手(版本/token), 成功后提供协议级方法。 */
public sealed class RemoteHostClient : IAsyncDisposable
{
    private readonly JsonRpcPeer _peer;

    private RemoteHostClient(JsonRpcPeer peer, HostHelloResponse hello)
    {
        _peer = peer;
        Hello = hello;
    }

    public HostHelloResponse Hello { get; }

    public static async Task<RemoteHostClient> ConnectAsync(
        Stream duplex,
        string? token = null,
        CancellationToken cancellationToken = default)
    {
        var peer = new JsonRpcPeer(duplex);
        peer.Start();
        try
        {
            var request = new HostHelloRequest(token, HostProtocol.Version);
            var parameters = JsonSerializer.SerializeToElement(request, HostProtocolJsonContext.Default.HostHelloRequest);
            var result = await peer.RequestAsync(HostProtocol.MethodHello, parameters, cancellationToken).ConfigureAwait(false);
            var hello = result is { } value
                ? JsonSerializer.Deserialize(value, HostProtocolJsonContext.Default.HostHelloResponse)
                : null;
            if (hello is null)
                throw new IOException("host returned an invalid hello");
            if (!hello.Ok)
                throw new IOException($"host handshake rejected: {hello.Mismatch}");
            return new RemoteHostClient(peer, hello);
        }
        catch
        {
            await peer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<long> PingAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPing, null, HostProtocolJsonContext.Default.Int64, cancellationToken);

    public Task<HostInfo> GetInfoAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodInfo, null, HostProtocolJsonContext.Default.HostInfo, cancellationToken);

    private async Task<T> SendAsync<T>(
        string method,
        JsonElement? parameters,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var result = await _peer.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        if (result is not { } value)
            throw new IOException($"host returned no result for {method}");
        return JsonSerializer.Deserialize(value, typeInfo)
            ?? throw new IOException($"host returned an invalid result for {method}");
    }

    public ValueTask DisposeAsync() => _peer.DisposeAsync();
}
