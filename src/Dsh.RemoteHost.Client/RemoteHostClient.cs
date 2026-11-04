using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * 本地侧连接远端 host: 先握手(版本/token), 然后实现 RemoteHost 客户端。
 * 会话事件/审批/PTY 输出经服务端推送(通知)转成本端的异步流。
 */
public sealed class RemoteHostClient : IAsyncDisposable, IWorkspaceHost
{
    private readonly JsonRpcPeer _peer;
    private readonly RemotePtyClient _pty;
    private readonly RemoteNotificationsClient _notifications;
    private readonly ConcurrentDictionary<string, Channel<RemoteSessionEvent>> _eventStreams = new(StringComparer.Ordinal);
    private readonly Channel<RemotePendingChange> _pendingChanges = Channel.CreateUnbounded<RemotePendingChange>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<RemoteSessionChange> _sessionChanges = Channel.CreateUnbounded<RemoteSessionChange>(new UnboundedChannelOptions { SingleReader = true });

    /** 推送处理在握手之前挂好: host 在 hello 应答之后立即补发未决请求, 晚挂会漏掉它们。 */
    private RemoteHostClient(JsonRpcPeer peer)
    {
        _peer = peer;
        Agent = new RemoteAgentClient(this);
        _pty = new RemotePtyClient(this);
        Files = new RemoteFilesClient(this);
        Git = new RemoteGitClient(this);
        _notifications = new RemoteNotificationsClient(this);
        SessionEditing = new RemoteSessionEditingClient(this);
        Plugins = new RemotePluginsClient(this);
        Network = new RemoteNetworkClient(this);
        Editor = new RemoteEditorClient(this);
        peer.RequestPreparation = Editor.Prepare;
        peer.NotificationReceived += OnNotification;
        peer.Closed += OnClosed;
    }

    public HostHelloResponse Hello { get; private set; } = new(false, HostProtocol.Version, "", null);
    public RemoteAgentClient Agent { get; }
    public RemotePtyClient Pty => _pty;
    public RemoteFilesClient Files { get; }
    public RemoteGitClient Git { get; }
    public RemoteNotificationsClient Notifications => _notifications;
    public RemoteSessionEditingClient SessionEditing { get; }
    public RemotePluginsClient Plugins { get; }
    public RemoteNetworkClient Network { get; }
    public RemoteEditorClient Editor { get; }
    IWorkspaceAgents IWorkspaceHost.Agent => Agent;
    public T? GetCapability<T>() where T : class
        => Supports(Dsh.Ide.Bridge.EditorProtocol.Capability) ? Editor as T : null;
    IWorkspaceFiles IWorkspaceHost.Files => Files;
    IWorkspaceGit IWorkspaceHost.Git => Git;
    IWorkspaceNotifications IWorkspaceHost.Notifications => Notifications;
    IWorkspaceSessionEditing IWorkspaceHost.SessionEditing => SessionEditing;
    IWorkspacePlugins IWorkspaceHost.Plugins => Plugins;
    IWorkspaceNetwork IWorkspaceHost.Network => Network;
    IWorkspaceTerminals IWorkspaceHost.Pty => Pty;

    internal bool IsClosed => _peer.IsClosed;

    /** Whether the host advertises a capability from HostProtocolCatalog. */
    public bool Supports(string capability) => Hello.Capabilities?.Contains(capability) == true;

    public Task WhenClosed => _closed.Task;

    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters, CancellationToken cancellationToken)
    {
        if (parameters is { ValueKind: JsonValueKind.Object } value
            && ((method == HostProtocol.MethodApprovalRespond
                    && value.TryGetProperty(nameof(RemoteApprovalResponse.Cancelled), out var cancelled)
                    && cancelled.ValueKind == JsonValueKind.True)
                || (method == HostProtocol.MethodQuestionRespond
                    && value.TryGetProperty(nameof(RemoteQuestionResponse.Status), out var status)
                    && status.ValueKind == JsonValueKind.String && status.ValueEquals("dismissed"))))
            RequireDecisionCancellation();
        return await _peer.RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException($"host returned no result for {method}");
    }

    public static Task<RemoteHostClient> ConnectAsync(
        Stream duplex,
        string? token = null,
        CancellationToken cancellationToken = default)
        => ConnectAsync(duplex, token, clientName: null, cancellationToken);

    /** ClientName 是本客户端的显示名(如 "gui@laptop"), host 用它标注由谁回答了审批/问答。 */
    public static async Task<RemoteHostClient> ConnectAsync(
        Stream duplex,
        string? token,
        string? clientName,
        CancellationToken cancellationToken)
    {
        var peer = new JsonRpcPeer(duplex);
        var client = new RemoteHostClient(peer);
        peer.Start();
        try
        {
            var request = new HostHelloRequest(token, HostProtocol.Version, clientName);
            var parameters = JsonSerializer.SerializeToElement(request, HostProtocolJsonContext.Default.HostHelloRequest);
            var result = await peer.RequestAsync(HostProtocol.MethodHello, parameters, cancellationToken).ConfigureAwait(false);
            var hello = result is { } value
                ? JsonSerializer.Deserialize(value, HostProtocolJsonContext.Default.HostHelloResponse)
                : null;
            if (hello is null)
                throw new IOException("host returned an invalid hello");
            if (!hello.Ok)
            {
                if (hello.ProtocolVersion != HostProtocol.Version)
                    throw new HostProtocolMismatchException(HostProtocol.Version, hello.ProtocolVersion, hello.Mismatch);
                throw new IOException($"host handshake rejected: {hello.Mismatch}");
            }
            if (hello.ProtocolVersion != HostProtocol.Version)
                throw new HostProtocolMismatchException(HostProtocol.Version, hello.ProtocolVersion);
            client.Hello = hello;
            return client;
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

    public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionList, null, HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionSummary, cancellationToken);

    public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionCreate, Serialize(new RemoteSessionCreateRequest(cwd), HostProtocolJsonContext.Default.RemoteSessionCreateRequest), HostProtocolJsonContext.Default.RemoteSessionSummary, cancellationToken);

    public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionResume, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.RemoteSessionSummary, cancellationToken);

    /** 整段历史按字节预算分页取完再拼成一个快照, 调用方看到的历史与单次响应时一致。 */
    public async Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var snapshot = await FetchSnapshotPageAsync(sessionId, 0, cancellationToken).ConfigureAwait(false);
        var events = new List<SessionEvent>(snapshot.Events);
        while (events.Count < snapshot.TotalEventCount)
        {
            var next = await FetchSnapshotPageAsync(sessionId, events.Count, cancellationToken).ConfigureAwait(false);
            // 分页期间历史被截断时整段也就到此为止。
            if (next.Events.Count == 0)
                break;
            events.AddRange(next.Events);
            snapshot = next;
        }
        // 拼好的快照就是整段历史: 条数按实到的事件重算, 免得把中途的旧总数带出去。
        return snapshot with { Events = events, TotalEventCount = events.Count };
    }

    private Task<RemoteSessionSnapshot> FetchSnapshotPageAsync(string sessionId, long fromEvent, CancellationToken cancellationToken)
        => SendAsync(HostProtocol.MethodSessionSnapshot,
            Serialize(new RemoteSessionSnapshotRequest(sessionId, fromEvent), HostProtocolJsonContext.Default.RemoteSessionSnapshotRequest),
            HostProtocolJsonContext.Default.RemoteSessionSnapshot,
            cancellationToken);

    public async IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<RemoteSessionEvent>(new UnboundedChannelOptions { SingleReader = true });
        _eventStreams[subscriptionId] = channel;
        try
        {
            await SendAsync(HostProtocol.MethodSessionSubscribe, Serialize(new RemoteSubscribeRequest(sessionId, fromSeq, subscriptionId), HostProtocolJsonContext.Default.RemoteSubscribeRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            _eventStreams.TryRemove(subscriptionId, out _);
            await StopStreamAsync(HostProtocol.MethodSessionUnsubscribe, subscriptionId).ConfigureAwait(false);
        }
    }

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionMessage, Serialize(new RemoteMessageRequest(sessionId, text, images), HostProtocolJsonContext.Default.RemoteMessageRequest), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionInterrupt, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.Boolean, cancellationToken);

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodToolsList, Serialize(new RemoteSessionRef(sessionId), HostProtocolJsonContext.Default.RemoteSessionRef), HostProtocolJsonContext.Default.IReadOnlyListString, cancellationToken);

    public IAsyncEnumerable<RemotePendingChange> PendingChangesAsync(CancellationToken cancellationToken = default)
        => _pendingChanges.Reader.ReadAllAsync(cancellationToken);

    public Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default, bool rememberForSession = false, bool cancelled = false)
    {
        return SendAsync(HostProtocol.MethodApprovalRespond, Serialize(new RemoteApprovalResponse(requestId, allow, reason, rememberForSession, cancelled), HostProtocolJsonContext.Default.RemoteApprovalResponse), HostProtocolJsonContext.Default.Boolean, cancellationToken);
    }


    public Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default, string status = "answered", string? reason = null)
    {
        return SendAsync(HostProtocol.MethodQuestionRespond, Serialize(new RemoteQuestionResponse(requestId, answers, status, reason), HostProtocolJsonContext.Default.RemoteQuestionResponse), HostProtocolJsonContext.Default.Boolean, cancellationToken);
    }

    private void RequireDecisionCancellation()
    {
        if (!Supports(DecisionProtocol.Capability))
            throw new NotSupportedException($"Unsupported: host does not support {DecisionProtocol.Capability}; the request is still pending. Upgrade the host to cancel it.");
    }


    public Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodPendingList, null, HostProtocolJsonContext.Default.RemotePendingList, cancellationToken);

    public Task<IReadOnlyList<RemoteSessionSummary>> ListHostSessionsAsync(CancellationToken cancellationToken = default)
        => SendAsync(HostProtocol.MethodSessionsList, null, HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionSummary, cancellationToken);

    public IAsyncEnumerable<RemoteSessionChange> HostSessionChangesAsync(CancellationToken cancellationToken = default)
        => _sessionChanges.Reader.ReadAllAsync(cancellationToken);

    private async Task StopStreamAsync(string method, string subscriptionId)
    {
        if (_peer.IsClosed)
            return;
        try
        {
            await SendAsync(method, Serialize(new RemoteSubscriptionRef(subscriptionId), HostProtocolJsonContext.Default.RemoteSubscriptionRef), HostProtocolJsonContext.Default.Boolean, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
        }
    }

    private void OnNotification(JsonRpcMessage message)
    {
        _pty.OnNotification(message);
        _notifications.OnNotification(message);
        switch (message.Method)
        {
            case HostProtocol.NotificationEvent when message.Params is { } eventParameters:
                var frame = JsonSerializer.Deserialize(eventParameters, HostProtocolJsonContext.Default.RemoteEventFrame);
                if (_eventStreams.TryGetValue(frame.SubscriptionId, out var events))
                    events.Writer.TryWrite(frame.Event);
                break;
            case HostProtocol.NotificationPendingChanged when Parse(message, HostProtocolJsonContext.Default.RemotePendingChange) is { } pending:
                _pendingChanges.Writer.TryWrite(pending);
                break;
            case HostProtocol.NotificationSessionsChanged when Parse(message, HostProtocolJsonContext.Default.RemoteSessionChange) is { } session:
                _sessionChanges.Writer.TryWrite(session);
                break;
        }
    }

    private void OnClosed(Exception? error)
    {
        var failure = error ?? new IOException("host connection closed");
        _pty.OnClosed(failure);
        _notifications.OnClosed(failure);
        _ = Editor.DisposeAsync();
        foreach (var stream in _eventStreams.Values)
            stream.Writer.TryComplete(failure);
        _pendingChanges.Writer.TryComplete(failure);
        _sessionChanges.Writer.TryComplete(failure);
        _closed.TrySetResult();
    }

    private static T? Parse<T>(JsonRpcMessage message, JsonTypeInfo<T> info)
        => message.Params is { } element ? JsonSerializer.Deserialize(element, info) : default;

    private static JsonElement Serialize<T>(T value, JsonTypeInfo<T> info) => JsonSerializer.SerializeToElement(value, info);

    internal async Task<T> SendAsync<T>(string method, JsonElement? parameters, JsonTypeInfo<T> info, CancellationToken cancellationToken)
    {
        var value = await RequestAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize(value, info) ?? throw new IOException($"host returned an invalid result for {method}");
    }

    public async ValueTask DisposeAsync()
    {
        await _peer.DisposeAsync().ConfigureAwait(false);
        await Editor.DisposeAsync().ConfigureAwait(false);
    }
}





