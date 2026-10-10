using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * 本地侧连接远端 host: 先握手(版本/token), 然后实现 IRemoteHost。
 * 会话事件/审批/PTY 输出经服务端推送(通知)转成本端的异步流。
 */
public sealed class RemoteHostClient : IRemoteHost, IAsyncDisposable
{
    private readonly JsonRpcPeer _peer;
    private readonly ConcurrentDictionary<string, Channel<RemoteEventInfo>> _eventStreams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Channel<RemotePtyOutput>> _ptyStreams = new(StringComparer.Ordinal);
    private Channel<RemoteApprovalRequest>? _approvals;
    private Channel<RemoteQuestionRequest>? _questions;

    private RemoteHostClient(JsonRpcPeer peer, HostHelloResponse hello)
    {
        _peer = peer;
        Hello = hello;
        peer.NotificationReceived += OnNotification;
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

    public Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodInfo, null, HostProtocolJsonContext.Default.HostInfo, cancellationToken);

    public Task<IReadOnlyList<RemoteSessionInfo>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionList, null, HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionInfo, cancellationToken);

    public Task<RemoteSessionInfo> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionCreate, Serialize(new RemoteSessionCreateRequest(cwd), HostProtocolJsonContext.Default.RemoteSessionCreateRequest), HostProtocolJsonContext.Default.RemoteSessionInfo, cancellationToken);

    public async Task<RemoteSessionInfo> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await SendAsync(HostProtocol.MethodSessionResume, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.RemoteSessionInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException error) when (error.Message.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            // 老版本宿主对已在跑的会话会拒绝 resume(非幂等); 视为成功, 从会话列表取信息。
            var sessions = await ListSessionsAsync(cancellationToken).ConfigureAwait(false);
            return sessions.FirstOrDefault(session => string.Equals(session.Id, sessionId, StringComparison.Ordinal))
                ?? new RemoteSessionInfo(sessionId, "", "Running", 0, null, null, 0);
        }
    }

    public async IAsyncEnumerable<RemoteEventInfo> SubscribeAsync(string sessionId, long fromSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 重新订阅前停掉服务端旧泵, 并换成新通道: 否则旧泵与新泵会同时推送, 事件翻倍。
        // 老版本宿主没有 session.unsubscribe: 尽力而为, MethodNotFound 时跳过(不影响订阅)。
        try
        {
            await SendAsync(HostProtocol.MethodSessionUnsubscribe, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException error) when (error.Code == JsonRpcPeer.MethodNotFound)
        {
        }
        var channel = Channel.CreateUnbounded<RemoteEventInfo>();
        _eventStreams[sessionId] = channel;
        await SendAsync(HostProtocol.MethodSessionSubscribe, Serialize(new RemoteSubscribeRequest(sessionId, fromSeq), HostProtocolJsonContext.Default.RemoteSubscribeRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public Task UnsubscribeAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionUnsubscribe, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionMessage, Serialize(new RemoteMessageRequest(sessionId, text, images), HostProtocolJsonContext.Default.RemoteMessageRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionInterrupt, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodToolsList, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.IReadOnlyListString, cancellationToken);

    public async IAsyncEnumerable<RemoteApprovalRequest> ApprovalsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = _approvals ??= Channel.CreateUnbounded<RemoteApprovalRequest>();
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public Task RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodApprovalRespond, Serialize(new RemoteApprovalResponse(requestId, allow, reason), HostProtocolJsonContext.Default.RemoteApprovalResponse), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public async IAsyncEnumerable<RemoteQuestionRequest> QuestionsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = _questions ??= Channel.CreateUnbounded<RemoteQuestionRequest>();
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public Task RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodQuestionRespond, Serialize(new RemoteQuestionResponse(requestId, answers), HostProtocolJsonContext.Default.RemoteQuestionResponse), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken = default)
    {
        var content = await SendAsync(HostProtocol.MethodFileRead, Serialize(new RemoteFilePath(path), HostProtocolJsonContext.Default.RemoteFilePath), HostProtocolJsonContext.Default.RemoteFileContent, cancellationToken).ConfigureAwait(false);
        return Convert.FromBase64String(content.Base64);
    }

    public Task WriteFileAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFileWrite, Serialize(new RemoteFileContent(path, Convert.ToBase64String(content.Span)), HostProtocolJsonContext.Default.RemoteFileContent), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<RemoteDirectoryListing> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodFileList, Serialize(new RemoteFilePath(path), HostProtocolJsonContext.Default.RemoteFilePath), HostProtocolJsonContext.Default.RemoteDirectoryListing, cancellationToken);

    public async Task<string> StartPtyAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var reference = await SendAsync(HostProtocol.MethodPtyStart, Serialize(new RemotePtyStartRequest(fileName, arguments), HostProtocolJsonContext.Default.RemotePtyStartRequest), HostProtocolJsonContext.Default.RemotePtyRef, cancellationToken).ConfigureAwait(false);
        return reference.PtyId;
    }

    public async Task AttachPtyAsync(string ptyId, Stream input, Stream output, CancellationToken cancellationToken = default)
    {
        var channel = _ptyStreams.GetOrAdd(ptyId, _ => Channel.CreateUnbounded<RemotePtyOutput>());
        await SendAsync(HostProtocol.MethodPtyAttach, Serialize(new RemotePtyRef(ptyId), HostProtocolJsonContext.Default.RemotePtyRef), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
        var pumpInput = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            try
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    await SendAsync(HostProtocol.MethodPtyWrite, Serialize(new RemotePtyInput(ptyId, Convert.ToBase64String(buffer.AsSpan(0, read))), HostProtocolJsonContext.Default.RemotePtyInput), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException)
            {
            }
        }, cancellationToken);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var bytes = Convert.FromBase64String(item.Base64);
                if (bytes.Length > 0)
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (item.Eof)
                    break;
            }
        }
        finally
        {
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            await pumpInput.ConfigureAwait(false);
        }
    }

    public Task StopPtyAsync(string ptyId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPtyStop, Serialize(new RemotePtyRef(ptyId), HostProtocolJsonContext.Default.RemotePtyRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    private void OnNotification(JsonRpcMessage message)
    {
        switch (message.Method)
        {
            case HostProtocol.NotificationEvent when Parse(message, HostProtocolJsonContext.Default.RemoteEventInfo) is { } item
                && _eventStreams.TryGetValue(item.SessionId, out var events):
                events.Writer.TryWrite(item);
                break;
            case HostProtocol.NotificationApproval when Parse(message, HostProtocolJsonContext.Default.RemoteApprovalRequest) is { } request:
                (_approvals ??= Channel.CreateUnbounded<RemoteApprovalRequest>()).Writer.TryWrite(request);
                break;
            case HostProtocol.NotificationQuestion when Parse(message, HostProtocolJsonContext.Default.RemoteQuestionRequest) is { } question:
                (_questions ??= Channel.CreateUnbounded<RemoteQuestionRequest>()).Writer.TryWrite(question);
                break;
            case HostProtocol.NotificationPtyOutput when Parse(message, HostProtocolJsonContext.Default.RemotePtyOutput) is { } output
                && _ptyStreams.TryGetValue(output.PtyId, out var pty):
                pty.Writer.TryWrite(output);
                break;
        }
    }

    private static T? Parse<T>(JsonRpcMessage message, JsonTypeInfo<T> info)
        => message.Params is { } element ? JsonSerializer.Deserialize(element, info) : default;

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);

    private async Task<T> SendAsync<T>(string method, JsonElement? parameters, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var result = await _peer.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        if (result is not { } value)
            throw new IOException($"host returned no result for {method}");
        return JsonSerializer.Deserialize(value, info) ?? throw new IOException($"host returned an invalid result for {method}");
    }

    public ValueTask DisposeAsync() => _peer.DisposeAsync();
}
