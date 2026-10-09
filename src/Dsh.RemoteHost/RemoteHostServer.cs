using System.Text.Json;
using Dsh.Transport;

namespace Dsh.RemoteHost;

public sealed record RemoteHostServerOptions(string? Token = null);

/**
 * 在一条双向流上服务一个客户端: 处理握手/探活/信息; 其余方法交 capabilityHandler。
 * 单实例单客户端; 连接断开即返回(远端的会话生命周期由宿主进程而非连接决定, 见 §14「本地退出不杀远端」)。
 */
public sealed class RemoteHostServer(
    RemoteHostServerOptions options,
    Func<JsonRpcMessage, CancellationToken, ValueTask<JsonElement?>>? capabilityHandler = null)
{
    private static readonly string HostVersion =
        typeof(RemoteHostServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public async Task ServeAsync(Stream duplex, CancellationToken cancellationToken = default)
    {
        await using var peer = new JsonRpcPeer(duplex);
        peer.RequestHandler = HandleAsync;
        peer.Start();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Closed += _ => closed.TrySetResult();
        using var registration = cancellationToken.Register(() => closed.TrySetResult());
        await closed.Task.ConfigureAwait(false);
    }

    private async ValueTask<JsonElement?> HandleAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (message.Method)
        {
            case HostProtocol.MethodHello:
                return HandleHello(message);
            case HostProtocol.MethodPing:
                return JsonSerializer.SerializeToElement(
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), HostProtocolJsonContext.Default.Int64);
            case HostProtocol.MethodInfo:
                return JsonSerializer.SerializeToElement(Info(), HostProtocolJsonContext.Default.HostInfo);
            default:
                if (capabilityHandler is not null)
                    return await capabilityHandler(message, cancellationToken).ConfigureAwait(false);
                throw new JsonRpcException(JsonRpcPeer.MethodNotFound, $"method not found: {message.Method}");
        }
    }

    private JsonElement HandleHello(JsonRpcMessage message)
    {
        var request = message.Params is { } parameters
            ? JsonSerializer.Deserialize(parameters, HostProtocolJsonContext.Default.HostHelloRequest)
            : null;
        var tokenOk = options.Token is null
            || string.Equals(request?.Token, options.Token, StringComparison.Ordinal);
        var versionOk = request?.ProtocolVersion == HostProtocol.Version;
        var mismatch = !tokenOk
            ? "invalid token"
            : (!versionOk
                ? $"protocol version mismatch: client {request?.ProtocolVersion}, host {HostProtocol.Version}"
                : null);
        return JsonSerializer.SerializeToElement(
            new HostHelloResponse(mismatch is null, HostProtocol.Version, HostVersion, mismatch),
            HostProtocolJsonContext.Default.HostHelloResponse);
    }

    private static HostInfo Info() => new(
        HostProtocol.Version,
        HostVersion,
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        RemoteHostEndpoint.DefaultRoot());
}
