using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Dsh.Sdk;

namespace Dsh.Acp;

/** ACP client shared by local sockets and external agent processes; the caller starts its transport after installing the client. */
public sealed class AcpClient : IAsyncDisposable
{
    private readonly JsonRpcLineTransport _transport;
    private readonly ConcurrentDictionary<string, AcpPermissionRequest> _pendingPermissions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _closedPrompts = new(StringComparer.Ordinal);
    private readonly Channel<AcpPermissionRequest> _permissionQueue = Channel.CreateUnbounded<AcpPermissionRequest>();

    public AcpClient(JsonRpcLineTransport transport)
    {
        _transport = transport;
        _transport.NotificationHandler = OnNotification;
        _transport.RequestHandler = OnRequestAsync;
        _ = ReleaseOnCloseAsync();
    }

    public event Action<AcpSessionUpdate>? SessionUpdate;

    /** Skip already-resolved entries: a turn can be cancelled before a consumer reads its permission request. */
    public ChannelReader<AcpPermissionRequest> PermissionRequests => _permissionQueue.Reader;

    public IReadOnlyList<AcpPermissionRequest> PendingPermissions => _pendingPermissions.Values.ToList();

    public Task<Exception?> Closed => _transport.Closed;

    public Task<AcpInitializeResponse> InitializeAsync(AcpImplementationInfo? clientInfo = null, CancellationToken cancellationToken = default)
        => CallAsync(AcpMethods.Initialize,
            new AcpInitializeRequest(AcpMethods.ProtocolVersion, AcpClientCapabilities.None, clientInfo),
            AcpJsonContext.Default.AcpInitializeRequest, AcpJsonContext.Default.AcpInitializeResponse, cancellationToken);

    public async Task AuthenticateAsync(string token, CancellationToken cancellationToken = default)
        => await _transport.RequestAsync(AcpMethods.Authenticate, new Dictionary<string, object?>
        {
            ["methodId"] = AcpMethods.LocalTokenAuthMethod,
            ["_meta"] = new Dictionary<string, object?> { ["token"] = token },
        }, cancellationToken).ConfigureAwait(false);

    public Task<AcpSessionResponse> NewSessionAsync(string cwd, CancellationToken cancellationToken = default)
        => SessionAsync(AcpMethods.NewSession, new AcpSessionRequest(Path.GetFullPath(cwd), []), cancellationToken);

    /** Requires CanLoadSession; history is replayed as updates before the response completes. */
    public Task<AcpSessionResponse> LoadSessionAsync(string sessionId, string cwd, CancellationToken cancellationToken = default)
        => SessionAsync(AcpMethods.LoadSession, new AcpSessionRequest(Path.GetFullPath(cwd), [], sessionId), cancellationToken);

    /** Requires CanResumeSession; restores context without replaying history. */
    public Task<AcpSessionResponse> ResumeSessionAsync(string sessionId, string cwd, CancellationToken cancellationToken = default)
        => SessionAsync(AcpMethods.ResumeSession, new AcpSessionRequest(Path.GetFullPath(cwd), [], sessionId), cancellationToken);

    public async Task<string> PromptAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _closedPrompts.TryRemove(sessionId, out _);
        try
        {
            var response = await CallAsync(AcpMethods.Prompt, new AcpPromptRequest(sessionId, [AcpTextContent.Of(text)]),
                AcpJsonContext.Default.AcpPromptRequest, AcpJsonContext.Default.AcpPromptResponse, cancellationToken).ConfigureAwait(false);
            return response.StopReason;
        }
        finally
        {
            CancelSessionPermissions(sessionId);
        }
    }

    /** Send the cancellation notification before releasing pending permission responses, as required by ACP. */
    public Task CancelAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _transport.Notify(AcpMethods.Cancel,
                JsonSerializer.SerializeToElement(new AcpSessionIdParams(sessionId), AcpJsonContext.Default.AcpSessionIdParams));
        }
        finally
        {
            CancelSessionPermissions(sessionId);
        }
        return Task.CompletedTask;
    }

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _transport.RequestAsync(AcpMethods.CloseSession,
            JsonSerializer.SerializeToElement(new AcpSessionIdParams(sessionId), AcpJsonContext.Default.AcpSessionIdParams), cancellationToken).ConfigureAwait(false);
        _closedPrompts.TryRemove(sessionId, out _);
    }

    /** A closed prompt seals buffered permissions until the next prompt starts. */
    private void CancelSessionPermissions(string sessionId)
    {
        _closedPrompts[sessionId] = 0;
        foreach (var permission in _pendingPermissions.Values)
            if (permission.SessionId == sessionId)
                permission.TryCancel();
    }

    private async Task<AcpSessionResponse> SessionAsync(string method, AcpSessionRequest request, CancellationToken cancellationToken)
    {
        var response = await CallAsync(method, request, AcpJsonContext.Default.AcpSessionRequest,
            AcpJsonContext.Default.AcpSessionResponse, cancellationToken).ConfigureAwait(false);
        return response.SessionId is null ? response with { SessionId = request.SessionId } : response;
    }

    private async Task<TResponse> CallAsync<TRequest, TResponse>(string method, TRequest request,
        JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken cancellationToken)
    {
        var result = await _transport.RequestAsync(method, JsonSerializer.SerializeToElement(request, requestType), cancellationToken).ConfigureAwait(false);
        return result is JsonElement element && element.Deserialize(responseType) is { } response
            ? response
            : throw new JsonRpcResponseError(-32603, $"{method}: the agent returned an empty result");
    }

    private void OnNotification(string method, JsonElement? parameters)
    {
        if (method != AcpMethods.ClientSessionUpdate || parameters is not { ValueKind: JsonValueKind.Object } value)
            return;
        var sessionId = AcpSessionUpdate.StringOf(value, "sessionId");
        if (sessionId is null || !value.TryGetProperty("update", out var update) || update.ValueKind != JsonValueKind.Object)
            return;
        var kind = AcpSessionUpdate.StringOf(update, "sessionUpdate") ?? "";
        SessionUpdate?.Invoke(new AcpSessionUpdate(sessionId, kind, update.Clone()));
    }

    private async Task<object?> OnRequestAsync(string method, JsonElement? parameters)
    {
        if (method != AcpMethods.ClientRequestPermission)
            throw new JsonRpcResponseError(-32601, $"method not found: {method}");
        var decoded = parameters?.Deserialize(AcpJsonContext.Default.AcpPermissionParams)
            ?? throw new JsonRpcResponseError(-32602, "empty permission request");
        var permission = new AcpPermissionRequest(decoded.SessionId, AcpPermissionRequest.ToolCallOf(decoded.ToolCall), decoded.Options);
        _pendingPermissions[permission.RequestId] = permission;
        using var registration = _transport.ConnectionClosed.Register(() => permission.TryCancel());
        if (_closedPrompts.ContainsKey(decoded.SessionId))
            permission.TryCancel();
        _permissionQueue.Writer.TryWrite(permission);
        try
        {
            var outcome = await permission.Outcome.ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new AcpPermissionResponse(outcome), AcpJsonContext.Default.AcpPermissionResponse);
        }
        finally
        {
            _pendingPermissions.TryRemove(permission.RequestId, out _);
        }
    }

    private async Task ReleaseOnCloseAsync()
    {
        await _transport.Closed.ConfigureAwait(false);
        foreach (var permission in _pendingPermissions.Values)
            permission.TryCancel();
        _permissionQueue.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync() => await _transport.DisposeAsync().ConfigureAwait(false);
}
