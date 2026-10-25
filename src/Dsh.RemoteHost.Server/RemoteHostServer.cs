using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

public sealed record RemoteHostServerOptions(string? Token = null);

/**
 * 在一条双向流上服务一个客户端: 处理握手/探活/信息与全部能力方法, 并把会话事件/审批/会话变化/PTY 输出推给客户端。
 * 同一个实例可同时服务多条连接: 每条连接独立订阅, 审批与会话变化广播到所有连接; 连接断开即返回(会话生命周期由宿主进程决定)。
 */
public sealed class RemoteHostServer(
    RemoteHostServerOptions options,
    IRemoteHostBackend? backend = null)
{
    /** JSON-RPC server-defined error code: a method other than hello was called before a successful hello. */
    private const int NotAuthenticated = -32001;

    private readonly IRemoteHostBackend? _backend = backend;
    private long _connectionSequence;

    private string? Token => options.Token;

    private bool RequiresToken => Token is not null;

    /** Capabilities describe the services this backend actually supplies, not protocol knowledge. */
    private readonly IReadOnlyList<string> _capabilities = CapabilitiesOf(backend);

    private IReadOnlyList<string> Capabilities => _capabilities;

    private static IReadOnlyList<string> CapabilitiesOf(IRemoteHostBackend? backend)
    {
        if (backend is null)
            return [];
        List<string> capabilities = [HostProtocol.PendingBroadcastCapability, HostProtocol.SessionRegistryCapability];
        if (backend.Agent is not null) capabilities.Add(AgentProtocol.Capability);
        if (backend.Pty is not null) capabilities.AddRange([PtyProtocol.Capability, PtyProtocol.ResizeCapability]);
        if (backend.Files is not null) capabilities.Add(FilesProtocol.Capability);
        if (backend.Git is not null) capabilities.Add(GitProtocol.Capability);
        if (backend.Notifications is not null) capabilities.Add(NotificationsProtocol.Capability);
        if (backend.SessionEditing is not null) capabilities.Add(SessionEditingProtocol.Capability);
        if (backend.Plugins is not null) capabilities.Add(PluginsProtocol.Capability);
        if (backend.Network is not null) capabilities.AddRange([NetworkProtocol.Capability, NetworkProtocol.CancellableProbeCapability]);
        if (backend.Network is IRemoteNetworkReloadBackend) capabilities.Add(NetworkProtocol.ReloadCapability);
        return capabilities;
    }

    public async Task ServeAsync(Stream duplex, CancellationToken cancellationToken = default)
    {
        await using var peer = new JsonRpcPeer(duplex);
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connection = new Connection(this, peer, $"c{Interlocked.Increment(ref _connectionSequence)}", pumps.Token);
        peer.RequestHandler = connection.HandleAsync;
        peer.RequestPreparation = connection.PrepareRequest;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.Closed += _ => closed.TrySetResult();
        try
        {
            // Register routes synchronously, then read EOF while a snapshot waits for its domain gate.
            var initialize = connection.StartIfTrustedAsync();
            peer.Start();
            using var registration = cancellationToken.Register(() => closed.TrySetResult());
            if (await Task.WhenAny(initialize, closed.Task).ConfigureAwait(false) == closed.Task)
            {
                await pumps.CancelAsync().ConfigureAwait(false);
                try
                {
                    await initialize.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (pumps.IsCancellationRequested)
                {
                }
                return;
            }
            await initialize.ConfigureAwait(false);
            await closed.Task.ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await pumps.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    connection.StopAll();
                }
                finally
                {
                    try
                    {
                        await connection.DisposeNetworkAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        await connection.Uploads.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private HostInfo StaticInfo() => new(
        HostProtocol.Version,
                HostProtocol.ReleaseVersion,
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
        RemoteHostEndpoint.DefaultRoot(),
        Capabilities);

    /** 一条连接的处理与推送状态。 */
    private sealed class Connection(RemoteHostServer server, JsonRpcPeer peer, string id, CancellationToken lifetime)
    {
        private delegate ValueTask<JsonElement?> DomainHandler(string name, JsonRpcMessage message, CancellationToken cancellationToken);
        private delegate ValueTask<JsonElement?>? DomainPreparation(string name, JsonRpcMessage message);
        private readonly Dictionary<string, (DomainHandler Handle, DomainPreparation? Prepare)> _domainHandlers = new(StringComparer.Ordinal);

        private readonly KeyedPumps _eventPumps = new(lifetime);
        private readonly KeyedPumps _ptyPumps = new(lifetime);
        private readonly List<HostSubscription> _subscriptions = [];
        private string? _clientName;

        /** With a host token every method but hello waits for a successful hello; without one the connection is trusted from the start. */
        private volatile bool _authenticated = !server.RequiresToken;
        private bool _stopped;
        private Task? _hostPushes;
        private HostNetworkMethods? _network;

        private PendingResolver Resolver => new(_clientName ?? id, id);

        /** Chunked uploads started on this connection; discarded when it ends. */
        public HostUploads Uploads { get; } = new();

        /** 无 token 的 host 从连接建立起就推送; 有 token 的等 hello 通过后再推, 未认证的连接收不到任何请求内容。 */
        public async Task StartIfTrustedAsync()
        {
            RegisterDomains();
            if (_authenticated)
                await StartHostPushesAsync().ConfigureAwait(false);
        }

        public void StopAll()
        {
            _eventPumps.StopAll();
            _ptyPumps.StopAll();
            lock (_subscriptions)
            {
                _stopped = true;
                foreach (var subscription in _subscriptions)
                    subscription.Dispose();
                _subscriptions.Clear();
            }
        }

        public ValueTask DisposeNetworkAsync() => _network?.DisposeAsync() ?? ValueTask.CompletedTask;

        /** Ownership changes run in frame order, before concurrent backend work can overtake a preceding subscribe. */
        public ValueTask<JsonElement?>? PrepareRequest(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            if (!_authenticated || server._backend is not { } backend)
                return null;
            switch (message.Method)
            {
                case HostProtocol.MethodSessionSubscribe:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSubscribeRequest, "subscribe requires sessionId");
                    return StartedAsync(_eventPumps.StartAsync(request.SubscriptionId,
                        token => backend.Session.SubscribeAsync(request.SessionId, request.FromSeq, token),
                        (events, token) => PumpAsync(events, HostProtocol.NotificationEvent,
                            item => new RemoteEventFrame(request.SubscriptionId, item), HostProtocolJsonContext.Default.RemoteEventFrame, token)));
                }
                case HostProtocol.MethodSessionUnsubscribe:
                    _eventPumps.Stop(Require(message, HostProtocolJsonContext.Default.RemoteSubscriptionRef, "unsubscribe requires subscriptionId").SubscriptionId);
                    return ValueTask.FromResult<JsonElement?>(True);
                default:
                    return _domainHandlers.TryGetValue(message.Method ?? "", out var domain)
                        ? domain.Prepare?.Invoke(message.Method!, message)
                        : null;
            }
        }

        private static async ValueTask<JsonElement?> StartedAsync(Task initialize)
        {
            await initialize.ConfigureAwait(false);
            return True;
        }

        public async ValueTask<JsonElement?> HandleAsync(JsonRpcMessage message, CancellationToken cancellationToken)
        {
            var name = message.Method ?? "";
            if (!_authenticated && name != HostProtocol.MethodHello)
                throw new JsonRpcException(NotAuthenticated, $"call {HostProtocol.MethodHello} with the host token before {name}");
            switch (name)
            {
                case HostProtocol.MethodHello:
                    return Serialize(await HelloAsync(message).ConfigureAwait(false), HostProtocolJsonContext.Default.HostHelloResponse);
                case HostProtocol.MethodPing:
                    return Serialize(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), HostProtocolJsonContext.Default.Int64);
                case HostProtocol.MethodInfo:
                    return Serialize(await InfoAsync(cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.HostInfo);
            }
            var backend = server._backend ?? throw MethodNotFound(name);
            if (_domainHandlers.TryGetValue(name, out var handler))
                return await handler.Handle(name, message, cancellationToken).ConfigureAwait(false);
            return await HandleBackendAsync(backend, name, message, cancellationToken).ConfigureAwait(false);
        }

        private void RegisterDomains()
        {
            if (server._backend is not { } backend)
                return;
            if (backend.Agent is { } agent)
                Register(AgentProtocol.Methods, (name, message, ct) => HostAgentMethods.HandleAsync(agent, name, message, ct));
            if (backend.Pty is { } pty)
            {
                var handler = new HostPtyMethods(pty, (subscriptionId, ptyId) => StartPtyPumpAsync(pty, subscriptionId, ptyId), _ptyPumps.Stop);
                Register(PtyProtocol.Methods, handler.HandleAsync, handler.Prepare);
            }
            if (backend.Files is { } files)
                Register(FilesProtocol.Methods, (name, message, ct) => HostFileMethods.HandleAsync(files, Uploads, name, message, ct));
            if (backend.Git is { } git)
                Register(GitProtocol.Methods, (name, message, ct) => HostGitMethods.HandleAsync(git, name, message, ct));
            if (backend.Notifications is { } notifications)
                Register(NotificationsProtocol.Methods, (name, message, ct) => HostNotifyMethods.HandleAsync(notifications, name, message, ct));
            if (backend.SessionEditing is { } editing)
                Register(SessionEditingProtocol.Methods, (name, message, ct) => HostSessionEditingMethods.HandleAsync(editing, name, message, ct));
            if (backend.Plugins is { } plugins)
                Register(PluginsProtocol.Methods, (name, message, ct) => HostPluginsMethods.HandleAsync(plugins, name, message, ct));
            if (backend.Network is { } network)
            {
                _network = new HostNetworkMethods(network, lifetime);
                Register(NetworkProtocol.Methods, _network.HandleAsync, _network.Prepare);
            }
            if (backend.Network is IRemoteNetworkReloadBackend reload)
                Register([NetworkProtocol.MethodReload], (_, message, ct) => HostNetworkMethods.ReloadAsync(reload, message, ct));
        }

        private void Register(IReadOnlyList<string> methods, DomainHandler handler, DomainPreparation? prepare = null)
        {
            foreach (var method in methods)
                _domainHandlers.Add(method, (handler, prepare));
        }

        private Task StartPtyPumpAsync(IRemotePtyBackend backend, string subscriptionId, string ptyId)
            => _ptyPumps.StartAsync(subscriptionId, token => backend.AttachPtyAsync(ptyId, token),
                (output, token) => PumpAsync(output, PtyProtocol.NotificationPtyOutput,
                    item => new RemotePtyFrame(subscriptionId, item), PtyProtocolJsonContext.Default.RemotePtyFrame, token));

        private async Task<HostHelloResponse> HelloAsync(JsonRpcMessage message)
        {
            var request = message.Params is { } parameters
                ? JsonSerializer.Deserialize(parameters, HostProtocolJsonContext.Default.HostHelloRequest)
                : null;
            var token = server.Token;
            var tokenOk = token is null
                || (request?.Token is { } presented
                    && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token)));
            var versionOk = request?.ProtocolVersion == HostProtocol.Version;
            var mismatch = !tokenOk
                ? "invalid token"
                : (!versionOk
                    ? $"protocol version mismatch: client {request?.ProtocolVersion}, host {HostProtocol.Version}"
                    : null);
            if (mismatch is null)
                await AuthenticateAsync(request?.ClientName).ConfigureAwait(false);
        return new HostHelloResponse(mismatch is null, HostProtocol.Version, HostProtocol.ReleaseVersion, mismatch, server.Capabilities, id);
        }

        private async Task AuthenticateAsync(string? clientName)
        {
            _clientName = string.IsNullOrWhiteSpace(clientName) ? null : clientName;
            if (_authenticated)
                return;
            await StartHostPushesAsync().ConfigureAwait(false);
            _authenticated = true;
        }

        private async Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => server._backend is { } backend
                ? (await backend.InfoAsync(cancellationToken).ConfigureAwait(false)) with { Capabilities = server.Capabilities }
                : server.StaticInfo();

        private async ValueTask<JsonElement?> HandleBackendAsync(IRemoteHostBackend backend, string name, JsonRpcMessage message, CancellationToken cancellationToken)
        {
            switch (name)
            {
                case HostProtocol.MethodSessionList:
                    return Serialize(await backend.Session.ListSessionsAsync(cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionSummary);
                case HostProtocol.MethodSessionCreate:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSessionCreateRequest, "create requires cwd");
                    return Serialize(await backend.Session.CreateSessionAsync(request.Cwd, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteSessionSummary);
                }
                case HostProtocol.MethodSessionResume:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, "resume requires sessionId");
                    return Serialize(await backend.Session.ResumeSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteSessionSummary);
                }
                case HostProtocol.MethodSessionSnapshot:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, "snapshot requires sessionId");
                    return Serialize(await backend.Session.GetSessionSnapshotAsync(request.SessionId, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.RemoteSessionSnapshot);
                }
                case HostProtocol.MethodSessionMessage:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteMessageRequest, "message requires sessionId and text");
                    await backend.Session.SendMessageAsync(request.SessionId, request.Text, request.Images ?? [], cancellationToken).ConfigureAwait(false);
                    return True;
                }
                case HostProtocol.MethodSessionInterrupt:
                    await backend.Session.InterruptAsync(Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, "interrupt requires sessionId").SessionId, cancellationToken).ConfigureAwait(false);
                    return True;
                case HostProtocol.MethodSessionsList:
                    return Serialize(backend.Sessions.List(), HostProtocolJsonContext.Default.IReadOnlyListRemoteSessionSummary);
                case HostProtocol.MethodToolsList:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteSessionRef, "tools requires sessionId");
                    return Serialize(await backend.Session.ListToolsAsync(request.SessionId, cancellationToken).ConfigureAwait(false), HostProtocolJsonContext.Default.IReadOnlyListString);
                }
                default:
                    return HandleDecisions(backend, name, message);
            }
        }

        private JsonElement HandleDecisions(IRemoteHostBackend backend, string name, JsonRpcMessage message)
        {
            switch (name)
            {
                case HostProtocol.MethodApprovalRespond:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteApprovalResponse, "approval requires requestId");
                    return Serialize(backend.Pending.RespondApproval(request, Resolver), HostProtocolJsonContext.Default.Boolean);
                }
                case HostProtocol.MethodQuestionRespond:
                {
                    var request = Require(message, HostProtocolJsonContext.Default.RemoteQuestionResponse, "question respond requires requestId");
                    return Serialize(backend.Pending.RespondQuestion(request, Resolver), HostProtocolJsonContext.Default.Boolean);
                }
                case HostProtocol.MethodPendingList:
                    return Serialize(backend.Pending.List(), HostProtocolJsonContext.Default.RemotePendingList);
                default:
                    throw MethodNotFound(name);
            }
        }


        private Task StartHostPushesAsync()
        {
            lock (_subscriptions)
                return _hostPushes ??= SubscribeHostPushesAsync();
        }

        private async Task SubscribeHostPushesAsync()
        {
            if (server._backend is not { } backend)
                return;
            List<HostSubscription> subscriptions = [];
            try
            {
                subscriptions.Add(backend.Pending.Subscribe());
                subscriptions.Add(backend.Sessions.Subscribe());
                if (backend.Notifications is { } notifications)
                    subscriptions.Add(await notifications.SubscribeAsync(lifetime).ConfigureAwait(false));
                lock (_subscriptions)
                {
                    if (_stopped)
                        throw new OperationCanceledException(lifetime);
                    _subscriptions.AddRange(subscriptions);
                }
            }
            catch
            {
                foreach (var subscription in subscriptions)
                    subscription.Dispose();
                throw;
            }
            foreach (var subscription in subscriptions)
                _ = PumpNotificationsAsync(subscription);
        }

        private async Task PumpNotificationsAsync(HostSubscription subscription)
        {
            try
            {
                await foreach (var notification in subscription.Reader.ReadAllAsync(lifetime).ConfigureAwait(false))
                    await peer.NotifyAsync(notification.Method, notification.Params, lifetime).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        private async Task PumpAsync<T, TFrame>(IAsyncEnumerable<T> items, string method, Func<T, TFrame> frame, JsonTypeInfo<TFrame> info, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var item in items.WithCancellation(cancellationToken).ConfigureAwait(false))
                    await peer.NotifyAsync(method, Serialize(frame(item), info), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }
    }

    /** 推送泵归属于订阅读者，释放一个读者不会影响同一会话或 PTY 的其他视图。 */
    private sealed class KeyedPumps(CancellationToken lifetime)
    {
        private readonly Dictionary<string, CancellationTokenSource> _pumps = new(StringComparer.Ordinal);
        private bool _stopped;

        /** Register before acquisition: detach owns cancellation even while the backend is awaiting its reader. */
        public async Task StartAsync<T>(string key,
            Func<CancellationToken, Task<RemoteHostStream<T>>> initialize,
            Func<IAsyncEnumerable<T>, CancellationToken, Task> pump)
        {
            CancellationTokenSource source;
            lock (_pumps)
            {
                if (_stopped)
                    throw new OperationCanceledException(lifetime);
                if (_pumps.Remove(key, out var existing))
                    existing.Cancel();
                source = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _pumps[key] = source;
            }
            // Registration is synchronous; backend acquisition must not occupy the frame reader.
            await Task.Yield();
            RemoteHostStream<T>? stream = null;
            var handedOff = false;
            try
            {
                source.Token.ThrowIfCancellationRequested();
                stream = await initialize(source.Token).ConfigureAwait(false);
                source.Token.ThrowIfCancellationRequested();
                handedOff = true;
                _ = RunAsync(key, source, stream, pump);
            }
            finally
            {
                if (!handedOff)
                {
                    try
                    {
                        if (stream is not null)
                            await stream.DisposeAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        Complete(key, source);
                    }
                }
            }
        }

        private async Task RunAsync<T>(string key, CancellationTokenSource source, RemoteHostStream<T> stream,
            Func<IAsyncEnumerable<T>, CancellationToken, Task> pump)
        {
            try
            {
                await using (((IAsyncDisposable)stream).ConfigureAwait(false))
                    await pump(stream, source.Token).ConfigureAwait(false);
            }
            finally
            {
                Complete(key, source);
            }
        }

        private void Complete(string key, CancellationTokenSource source)
        {
            lock (_pumps)
            {
                if (_pumps.TryGetValue(key, out var current) && ReferenceEquals(current, source))
                    _pumps.Remove(key);
                source.Dispose();
            }
        }

        public void Stop(string key)
        {
            lock (_pumps)
                if (_pumps.Remove(key, out var source))
                    source.Cancel();
        }

        public void StopAll()
        {
            lock (_pumps)
            {
                _stopped = true;
                var sources = _pumps.Values.ToArray();
                _pumps.Clear();
                foreach (var source in sources)
                    source.Cancel();
            }
        }
    }
}



