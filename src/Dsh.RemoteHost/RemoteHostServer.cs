using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Transport;

namespace Dsh.RemoteHost;

public sealed record RemoteHostServerOptions(string? Token = null);

/**
 * 在一条双向流上服务一个客户端: 处理握手/探活/信息与全部能力方法, 并把会话事件/审批/PTY 输出推给客户端。
 * 单实例单客户端; 连接断开即返回(远端会话生命周期由宿主进程决定, 不随本地连接结束)。
 */
public sealed class RemoteHostServer(
    RemoteHostServerOptions options,
    IRemoteHostBackend? backend = null)
{
    private static readonly string HostVersion =
        typeof(RemoteHostServer).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private readonly IRemoteHostBackend? _backend = backend;

    public async Task ServeAsync(Stream duplex, CancellationToken cancellationToken = default)
    {
        await using var peer = new JsonRpcPeer(duplex);
        var connection = new Connection(this, peer);
        peer.RequestHandler = connection.HandleAsync;
        peer.Start();
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connection.StartPumps(pumps.Token);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Closed += _ => closed.TrySetResult();
        using var registration = cancellationToken.Register(() => closed.TrySetResult());
        await closed.Task.ConfigureAwait(false);
        await pumps.CancelAsync().ConfigureAwait(false);
    }

    private JsonElement Hello(JsonRpcMessage message)
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
        return Serialize(new HostHelloResponse(mismatch is null, HostProtocol.Version, HostVersion, mismatch),
            HostProtocolJsonContext.Default.HostHelloResponse);
    }

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info)
        => JsonSerializer.SerializeToElement(value, info);

    private static HostInfo StaticInfo() => new(
        HostProtocol.Version,
        HostVersion,
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        RemoteHostEndpoint.DefaultRoot());

    private static T? Parse<T>(JsonElement? parameters, JsonTypeInfo<T> info)
        => parameters is { } element ? JsonSerializer.Deserialize(element, info) : default;

    /** 每个连接的处理与推送状态。 */
    private sealed class Connection(RemoteHostServer server, JsonRpcPeer peer)
    {

        public void StartPumps(CancellationToken cancellationToken)
        {
            _ = PumpApprovalsAsync(cancellationToken);
        }

        public async ValueTask<JsonElement?> HandleAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            var name = message.Method;
            var backend = server._backend;
            switch (name)
            {
                case HostProtocol.MethodHello:
                    return server.Hello(message);
                case HostProtocol.MethodPing:
                    return Serialize(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), HostProtocolJsonContext.Default.Int64);
                case HostProtocol.MethodInfo:
                    return Serialize(
                        backend is null ? StaticInfo() : await backend.InfoAsync(cancellationToken).ConfigureAwait(false),
                        HostProtocolJsonContext.Default.HostInfo);

                case HostProtocol.MethodSessionList when backend is not null:
                    return Serialize(await backend.ListSessionsAsync(cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionInfo);
                case HostProtocol.MethodSessionCreate when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteSessionCreateRequest)
                        ?? throw new JsonRpcException(-32602, "create requires cwd");
                    return Serialize(await backend.CreateSessionAsync(request.Cwd, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteSessionInfo);
                }
                case HostProtocol.MethodSessionResume when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteSessionRef)
                        ?? throw new JsonRpcException(-32602, "resume requires sessionId");
                    return Serialize(await backend.ResumeSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteSessionInfo);
                }
                case HostProtocol.MethodSessionSubscribe when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteSessionRef)
                        ?? throw new JsonRpcException(-32602, "subscribe requires sessionId");
                    _ = PumpEventsAsync(request.SessionId, cancellationToken);
                    return Serialize(true, HostProtocolJsonContext.Default.Boolean);
                }
                case HostProtocol.MethodSessionMessage when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteMessageRequest)
                        ?? throw new JsonRpcException(-32602, "message requires sessionId and text");
                    await backend.SendMessageAsync(request.SessionId, request.Text, cancellationToken).ConfigureAwait(false);
                    return Serialize(true, HostProtocolJsonContext.Default.Boolean);
                }
                case HostProtocol.MethodSessionInterrupt when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteSessionRef)
                        ?? throw new JsonRpcException(-32602, "interrupt requires sessionId");
                    await backend.InterruptAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
                    return Serialize(true, HostProtocolJsonContext.Default.Boolean);
                }

                case HostProtocol.MethodToolsList when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteSessionRef)
                        ?? throw new JsonRpcException(-32602, "tools requires sessionId");
                    return Serialize(await backend.ListToolsAsync(request.SessionId, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.IReadOnlyListString);
                }
                case HostProtocol.MethodApprovalRespond when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteApprovalResponse)
                        ?? throw new JsonRpcException(-32602, "approval requires requestId");
                    await backend.RespondApprovalAsync(request.RequestId, request.Allow, request.Reason, cancellationToken).ConfigureAwait(false);
                    return Serialize(true, HostProtocolJsonContext.Default.Boolean);
                }

                case HostProtocol.MethodFileRead when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteFilePath)
                        ?? throw new JsonRpcException(-32602, "read requires path");
                    var bytes = await backend.ReadFileAsync(request.Path, cancellationToken).ConfigureAwait(false);
                    return Serialize(new RemoteFileContent(request.Path, Convert.ToBase64String(bytes)), HostProtocolJsonContext.Default.RemoteFileContent);
                }
                case HostProtocol.MethodFileWrite when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemoteFileContent)
                        ?? throw new JsonRpcException(-32602, "write requires path and content");
                    await backend.WriteFileAsync(request.Path, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
                    return Serialize(true, HostProtocolJsonContext.Default.Boolean);
                }

                case HostProtocol.MethodPtyStart when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemotePtyStartRequest)
                        ?? throw new JsonRpcException(-32602, "pty start requires fileName");
                    var id = await backend.StartPtyAsync(request.FileName, request.Arguments, cancellationToken).ConfigureAwait(false);
                    return Serialize(new RemotePtyRef(id), HostProtocolJsonContext.Default.RemotePtyRef);
                }
                case HostProtocol.MethodPtyWrite when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemotePtyInput)
                        ?? throw new JsonRpcException(-32602, "pty write requires ptyId");
                    await backend.WritePtyAsync(request.PtyId, Convert.FromBase64String(request.Base64), cancellationToken).ConfigureAwait(false);
                    return Serialize(true, HostProtocolJsonContext.Default.Boolean);
                }
                case HostProtocol.MethodPtyStop when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemotePtyRef)
                        ?? throw new JsonRpcException(-32602, "pty stop requires ptyId");
                    await backend.StopPtyAsync(request.PtyId, cancellationToken).ConfigureAwait(false);
                    return Serialize(true, HostProtocolJsonContext.Default.Boolean);
                }
                case HostProtocol.MethodPtyAttach when backend is not null:
                {
                    var request = Parse(message.Params, HostProtocolJsonContext.Default.RemotePtyRef)
                        ?? throw new JsonRpcException(-32602, "pty attach requires ptyId");
                    _ = PumpPtyAsync(request.PtyId, cancellationToken);
                    return Serialize(true, HostProtocolJsonContext.Default.Boolean);
                }

                default:
                    throw new JsonRpcException(JsonRpcPeer.MethodNotFound, $"method not found: {name}");
            }
        }

        private async Task PumpEventsAsync(string sessionId, CancellationToken cancellationToken)
        {
            var backend = server._backend;
            if (backend is null)
                return;
            try
            {
                await foreach (var item in backend.SubscribeAsync(sessionId, cancellationToken).ConfigureAwait(false))
                    await peer.NotifyAsync(HostProtocol.NotificationEvent, Serialize(item, HostProtocolJsonContext.Default.RemoteEventInfo), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
            }
        }

        private async Task PumpApprovalsAsync(CancellationToken cancellationToken)
        {
            var backend = server._backend;
            if (backend is null)
                return;
            try
            {
                await foreach (var request in backend.ApprovalsAsync(cancellationToken).ConfigureAwait(false))
                    await peer.NotifyAsync(HostProtocol.NotificationApproval, Serialize(request, HostProtocolJsonContext.Default.RemoteApprovalRequest), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
            }
        }

        private async Task PumpPtyAsync(string ptyId, CancellationToken cancellationToken)
        {
            var backend = server._backend;
            if (backend is null)
                return;
            try
            {
                await foreach (var output in backend.PtyOutputAsync(ptyId, cancellationToken).ConfigureAwait(false))
                    await peer.NotifyAsync(HostProtocol.NotificationPtyOutput, Serialize(output, HostProtocolJsonContext.Default.RemotePtyOutput), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
            }
        }
    }
}
