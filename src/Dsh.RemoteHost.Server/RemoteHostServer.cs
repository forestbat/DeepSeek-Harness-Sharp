using StreamJsonRpc;
using System.Security.Cryptography;
using System.Text;
using Dsh.Core;
using Dsh.GitInspect;
using Dsh.Ide;
using Dsh.Ide.Bridge;
using Dsh.RemoteHost.Rpc;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * 服务端选项。
 * HiddenCapabilities 用于"能力已实现但暂不通告"的场景(滚动升级期间的老客户端兼容), 方法本身照常可用。
 *
 * SnapshotPageBytes 是**服务端**一页历史的字节预算(客户端请求里的 MaxBytes 只能更小): 一页装不下就截断,
 * 由客户端按序号续页。它不是协议常量而是宿主策略, 因此可以按部署/传输方式调小(例如走浏览器的中继),
 * 也便于用例在很小的数据上复现"一页被截断"。
 */
public sealed record RemoteHostServerOptions(
    string? Token = null,
    IReadOnlyList<string>? HiddenCapabilities = null,
    int SnapshotPageBytes = HostProtocol.SnapshotPageBytes);

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

    /** JSON-RPC server-defined error code: the connection's role is not permitted to perform the requested operation. */
    public const int Unauthorized = -32003;

    private readonly IRemoteHostBackend? _backend = backend;
    private long _connectionSequence;

    private string? Token => options.Token;

    private bool RequiresToken => Token is not null;

    /** 非回环监听必须先有 token; 监听器用它拒绝"裸端口直接暴露全部能力"。 */
    public bool HasToken => Token is { Length: > 0 };

    /** 一页历史的服务端字节预算(客户端请求里的 MaxBytes 只能更小)。 */
    internal int PageBudget => options.SnapshotPageBytes;

    /** Capabilities describe the services this backend actually supplies, not protocol knowledge. */
    private readonly IReadOnlyList<string> _capabilities = Hidden(CapabilitiesOf(backend), options.HiddenCapabilities);

    private static IReadOnlyList<string> Hidden(IReadOnlyList<string> capabilities, IReadOnlyList<string>? hidden)
        => hidden is not { Count: > 0 } ? capabilities : [.. capabilities.Where(capability => !hidden.Contains(capability, StringComparer.Ordinal))];

    private IReadOnlyList<string> Capabilities => _capabilities;

    private static IReadOnlyList<string> CapabilitiesOf(IRemoteHostBackend? backend)
    {
        if (backend is null)
            return [];
        List<string> capabilities = [HostProtocol.PendingBroadcastCapability, HostProtocol.SessionRegistryCapability, DecisionProtocol.Capability];
        if (backend.Agent is not null) capabilities.Add(AgentProtocol.Capability);
        if (backend.Pty is not null) capabilities.AddRange([PtyProtocol.Capability, PtyProtocol.ResizeCapability]);
        if (backend.Files is not null) capabilities.Add(FilesProtocol.Capability);
        if (backend.Git is not null) capabilities.Add(GitProtocol.Capability);
        if (backend.Notifications is not null) capabilities.Add(NotificationsProtocol.Capability);
        if (backend.SessionEditing is not null) capabilities.Add(SessionEditingProtocol.Capability);
        if (backend.Plugins is not null) capabilities.Add(PluginsProtocol.Capability);
        if (backend.Network is not null) capabilities.AddRange([NetworkProtocol.Capability, NetworkProtocol.CancellableProbeCapability]);
        if (backend.Network is IRemoteNetworkReloadBackend) capabilities.Add(NetworkProtocol.ReloadCapability);
        if (backend.Editor is not null) capabilities.Add(EditorProtocol.Capability);
        if (backend.Editor?.Clients is not null) capabilities.Add(EditorProtocol.ClientCapability);
        if (backend.Session is IRemoteLogSliceBackend) capabilities.Add(HostProtocol.LogSliceCapability);
        if (backend.Mesh is { } mesh)
        {
            if (mesh.SupportsMesh) capabilities.Add(MeshProtocol.CapabilityMesh);
            if (mesh.SupportsRooms) capabilities.Add(MeshProtocol.CapabilityRooms);
            if (mesh.SupportsDiscovery) capabilities.Add(MeshProtocol.CapabilityDiscovery);
            if (mesh.SupportsChannels) capabilities.Add(MeshProtocol.CapabilityChannels);
        }
        return capabilities;
    }

    public async Task ServeAsync(Stream duplex, CancellationToken cancellationToken = default)
    {
        using var pumps = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var connection = new Connection(this, $"c{Interlocked.Increment(ref _connectionSequence)}", pumps.Token);
        try
        {
            using (var channel = JsonRpcChannel.Serve(duplex, HostRpcFormatter.CreateJson(EditorProtocolJsonContext.Default),
                contracts: [new(RpcTargetMetadata.FromShape<IHostRpc>(), connection), ..connection.CreateTargets()], methods: connection.EditorMethods()))
            {
                connection.AttachPush(channel.AttachGenerated<IHostPush>());
                connection.AttachEditorChannel(channel);
                // 注册路由后立刻开始服务, 会话快照等自己的域慢慢取, 不占住读循环。
                await connection.StartIfTrustedAsync().ConfigureAwait(false);
                using var registration = cancellationToken.Register(() => connection.StopAll());
                try
                {
                    // 等待这条连接自己结束, 同时让宿主的取消也能结束它。
                    // Completion 只在传输层读到结尾时完成, 而停掉事件泵既不关闭 socket, 也不让对端不再 ping ——
                    // 监听被取消(宿主下线)时如果只等 Completion, 这里就永远不会返回, 下面 using 里的关闭也就永远轮不到,
                    // 客户端会一直以为自己连着旧宿主。取消即返回, 关闭交给 using。
                    await channel.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is ConnectionLostException or IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // 对端断开就是这条连接的正常结束: 连接死了由调用方各自的重连逻辑处理, 不从这里往上报。
                }
            }
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
                        await connection.DisposeDomainsAsync().ConfigureAwait(false);
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

    private enum HostAction
    {
        Read,
        SendMessage,
        MutateSession,
        Approve,
        Admin,
    }

    /** 一条连接的处理与推送状态。 */
    private sealed class Connection(RemoteHostServer server, string id, CancellationToken lifetime) : IHostRpc
    {
        private readonly KeyedPumps _eventPumps = new(lifetime);
        private readonly KeyedPumps _ptyPumps = new(lifetime);
        private readonly List<HostSubscription> _subscriptions = [];
        private string? _clientName;
        private string? _principalId;
        private RoomRole _role = RoomRole.Owner;
        private bool _inviteAuthenticated;
        private bool _allowCollaboratorApprovals;

        /** With a host token every method but hello waits for a successful hello; without one the connection is trusted from the start. */
        private volatile bool _authenticated = !server.RequiresToken;
        private bool _stopped;
        private Task? _hostPushes;
        private RoleGuardedNetworkRpc? _network;
        private HostEditorMethods? _editor;
        private IHostPush? _push;

        private PendingResolver Resolver => _inviteAuthenticated || _principalId is not null
            ? new(_clientName ?? _principalId ?? id, id, _principalId ?? _clientName, _role)
            : new(_clientName ?? id, id);

        private HostMeshCaller MeshCaller => new(
            id,
            _principalId ?? _clientName ?? id,
            _clientName ?? _principalId,
            _role);

        /** Chunked uploads started on this connection; discarded when it ends. */
        public HostUploads Uploads { get; } = new();

        public void AttachPush(IHostPush push) => _push = push;

        /** 反向调用的代理要等连接建好才能取, 所以域在第一次真正用到时才去读它。 */
        public void AttachEditorChannel(JsonRpcChannel channel) => _editor?.AttachChannel(channel);


        /** 各域自己的 RPC 目标; 只有 backend 提供了该域时才注册, 客户端据此走能力协商。 */
        /** 编辑器域的方法名委托; 域没有编辑器后端时为空。 */
        public (string Method, Delegate Handler)[] EditorMethods()
        {
            if (_editor is not { } editor)
                return [];
            return
            [
                (EditorProtocol.MethodList, (Func<RemoteEditorList?, CancellationToken, Task<EditorRemoteList>>)((request, ct) => { Authorize(HostAction.Read); return editor.ListAsync(request, ct); })),
                (EditorProtocol.MethodStatus, (Func<RemoteEditorWorkspace, CancellationToken, Task<EditorRemoteStatus>>)((request, ct) => { Authorize(HostAction.Read); return editor.StatusAsync(request, ct); })),
                (EditorProtocol.MethodDoctor, (Func<RemoteEditorWorkspace, CancellationToken, Task<IReadOnlyList<EditorDiagnostic>>>)((request, ct) => { Authorize(HostAction.Read); return editor.DoctorAsync(request, ct); })),
                (EditorProtocol.MethodOpen, (Func<RemoteEditorOpen, CancellationToken, Task<EditorResult>>)((request, ct) => { Authorize(HostAction.MutateSession); return editor.OpenAsync(request, ct); })),
                (EditorProtocol.MethodCancel, (Func<RemoteEditorCancel, CancellationToken, Task<bool>>)((request, ct) => { Authorize(HostAction.MutateSession); return editor.CancelAsync(request, ct); })),
                (EditorProtocol.MethodRegister, (Func<RemoteEditorClientRegistration, CancellationToken, Task<bool>>)((request, ct) => { Authorize(HostAction.MutateSession); return editor.RegisterAsync(request, ct); })),
                (EditorProtocol.MethodSelect, (Func<RemoteEditorSelect, CancellationToken, Task<bool>>)((request, ct) => { Authorize(HostAction.MutateSession); return editor.SelectAsync(request, ct); })),
                (EditorProtocol.MethodUnregister, (Func<CancellationToken, Task<bool>>)(ct => { Authorize(HostAction.MutateSession); return editor.UnregisterAsync(ct); })),
            ];
        }

        public IReadOnlyList<JsonRpcTarget> CreateTargets()
        {
            if (server._backend is not { } backend)
                return [];
            List<JsonRpcTarget> targets = [];
            var uploads = Uploads;
            if (backend.Agent is { } agent)
                targets.Add(new(RpcTargetMetadata.FromShape<IAgentRpc>(), new RoleGuardedAgentRpc(new HostAgentMethods(agent, EnsureAuthenticated), Authorize)));
            if (backend.Pty is { } pty)
                targets.Add(new(RpcTargetMetadata.FromShape<IPtyRpc>(), new RoleGuardedPtyRpc(new HostPtyMethods(pty, EnsureAuthenticated, (subscriptionId, ptyId) => StartPtyPumpAsync(pty, subscriptionId, ptyId), _ptyPumps.Stop), Authorize)));
            if (backend.Files is { } files)
                targets.Add(new(RpcTargetMetadata.FromShape<IFilesRpc>(), new RoleGuardedFilesRpc(new HostFileMethods(files, uploads, EnsureAuthenticated), Authorize)));
            if (backend.Git is { } git)
                targets.Add(new(RpcTargetMetadata.FromShape<IGitRpc>(), new RoleGuardedGitRpc(new HostGitMethods(git, EnsureAuthenticated), Authorize)));
            if (backend.Notifications is { } notifications)
                targets.Add(new(RpcTargetMetadata.FromShape<INotificationsRpc>(), new RoleGuardedNotificationsRpc(new HostNotifyMethods(notifications, EnsureAuthenticated), Authorize)));
            if (backend.SessionEditing is { } editing)
                targets.Add(new(RpcTargetMetadata.FromShape<ISessionEditingRpc>(), new RoleGuardedSessionEditingRpc(new HostSessionEditingMethods(editing, EnsureAuthenticated), Authorize)));
            if (backend.Plugins is { } plugins)
                targets.Add(new(RpcTargetMetadata.FromShape<IPluginsRpc>(), new RoleGuardedPluginsRpc(new HostPluginsMethods(plugins, EnsureAuthenticated), Authorize)));
            if (backend.Network is { } network)
            {
                _network = new RoleGuardedNetworkRpc(new HostNetworkMethods(network, EnsureAuthenticated, network as IRemoteNetworkReloadBackend, lifetime), Authorize);
                targets.Add(new(RpcTargetMetadata.FromShape<INetworkRpc>(), _network));
            }
            if (backend.Editor is { } editor)
            {
                _editor = new HostEditorMethods(editor, EnsureAuthenticated, id, lifetime);
            }
            if (backend.Mesh is { } mesh)
                targets.Add(new(RpcTargetMetadata.FromShape<IMeshRpc>(), new HostMeshMethods(mesh, Authorize, () => MeshCaller)));
            return targets;
        }

        /** 无 token 的 host 从连接建立起就推送; 有 token 的等 hello 通过后再推, 未认证的连接收不到任何请求内容。 */
        public async Task StartIfTrustedAsync()
        {
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

        public async ValueTask DisposeDomainsAsync()
        {
            try { if (_editor is not null) await _editor.DisposeAsync().ConfigureAwait(false); }
            finally { if (_network is not null) await _network.DisposeAsync().ConfigureAwait(false); }
        }

        private IRemoteHostBackend Backend => server._backend ?? throw new LocalRpcException("this host serves no backend") { ErrorCode = JsonRpcException.InvalidParams };

        /** 除 hello 之外的方法都要先通过握手。 */
        private void EnsureAuthenticated()
        {
            if (!_authenticated)
                throw new LocalRpcException($"call {HostProtocol.MethodHello} with the host token first") { ErrorCode = NotAuthenticated };
        }

        private void Authorize(HostAction action)
        {
            EnsureAuthenticated();
            var allowed = _role switch
            {
                RoomRole.Owner => true,
                RoomRole.Approver => action != HostAction.Admin,
                RoomRole.Collaborator => action switch
                {
                    HostAction.Read or HostAction.SendMessage or HostAction.MutateSession => true,
                    HostAction.Approve => _allowCollaboratorApprovals,
                    _ => false,
                },
                _ => action == HostAction.Read,
            };
            if (!allowed)
                throw new LocalRpcException($"role {_role} is not authorized to perform this operation") { ErrorCode = Unauthorized };
        }

        public async Task<HostHelloResponse> HelloAsync(HostHelloRequest request, CancellationToken cancellationToken)
        {
            var token = server.Token;
            var hasInvite = !string.IsNullOrWhiteSpace(request.InviteToken);
            var explicitTokenValid = request.Token is { } presented
                && token is not null
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token));
            var versionOk = request.ProtocolVersion == HostProtocol.Version;
            HostConnectionIdentity? invited = null;
            string? mismatch = null;

            if (!versionOk)
                mismatch = $"protocol version mismatch: client {request.ProtocolVersion}, host {HostProtocol.Version}";
            else if (token is not null && request.Token is not null && !explicitTokenValid)
                mismatch = "invalid token";
            else if (hasInvite)
            {
                invited = server._backend?.Mesh is { } mesh
                    ? await mesh.ResolveInviteAsync(request.InviteToken!, request.PrincipalId, request.ClientName, cancellationToken).ConfigureAwait(false)
                    : null;
                if (invited is null)
                    mismatch = "invalid invite token";
            }
            else if (token is not null && !explicitTokenValid)
                mismatch = "invalid token";

            if (mismatch is null)
            {
                await AuthenticateAsync(request.ClientName, request.PrincipalId, invited).ConfigureAwait(false);
                return new HostHelloResponse(true, HostProtocol.Version, HostProtocol.ReleaseVersion, null, server.Capabilities, id, _role.ToString());
            }
            return new HostHelloResponse(false, HostProtocol.Version, HostProtocol.ReleaseVersion, mismatch, server.Capabilities, id);
        }

        private async Task AuthenticateAsync(string? clientName, string? principalId, HostConnectionIdentity? invited)
        {
            _clientName = string.IsNullOrWhiteSpace(clientName) ? null : clientName;
            if (invited is not null)
            {
                _inviteAuthenticated = true;
                _principalId = invited.PrincipalId;
                _role = invited.Role;
                _allowCollaboratorApprovals = invited.AllowCollaboratorApprovals;
            }
            else
            {
                _inviteAuthenticated = false;
                _principalId = string.IsNullOrWhiteSpace(principalId) ? null : principalId;
                _role = RoomRole.Owner;
                _allowCollaboratorApprovals = false;
            }
            if (_authenticated)
                return;
            await StartHostPushesAsync().ConfigureAwait(false);
            _authenticated = true;
        }

        public Task<long> PingAsync(CancellationToken cancellationToken)
            => Task.FromResult(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        public async Task<HostInfo> InfoAsync(CancellationToken cancellationToken)
            => server._backend is { } backend
                ? (await backend.InfoAsync(cancellationToken).ConfigureAwait(false)) with { Capabilities = server.Capabilities }
                : server.StaticInfo();

        public async Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken)
        {
            Authorize(HostAction.Read);
            return await Backend.Session.ListSessionsAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteSessionSummary> CreateSessionAsync(RemoteSessionCreateRequest request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.MutateSession);
            return await Backend.Session.CreateSessionAsync(request.Cwd, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteSessionSummary> ResumeSessionAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.MutateSession);
            return await Backend.Session.ResumeSessionAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<RemoteSubscriptionRef> SubscribeAsync(RemoteSubscribeRequest request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.Read);
            var backend = Backend;
            await _eventPumps.StartAsync(request.SubscriptionId,
                token => backend.Session.SubscribeAsync(request.SessionId, request.FromSeq, token),
                (events, token) => PumpEventsAsync(request.SubscriptionId, events, token)).ConfigureAwait(false);
            return new RemoteSubscriptionRef(request.SubscriptionId);
        }

        public Task<bool> UnsubscribeAsync(RemoteSubscriptionRef request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.Read);
            _eventPumps.Stop(request.SubscriptionId);
            return Task.FromResult(true);
        }

        public async Task<bool> SendMessageAsync(RemoteMessageRequest request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.SendMessage);
            await Backend.Session.SendMessageAsync(request.SessionId, request.Text, request.Images ?? [], cancellationToken).ConfigureAwait(false);
            return true;
        }

        public async Task<bool> InterruptAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.MutateSession);
            await Backend.Session.InterruptAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        public async Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(RemoteSessionSnapshotRequest request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.Read);
            var backend = Backend;
            var snapshot = await backend.Session
                .GetSessionSnapshotAsync(request.SessionId, request.FromEvent, request.MaxEvents, cancellationToken)
                .ConfigureAwait(false);
            return RemoteSnapshotPage.Slice(
                snapshot,
                request.FromEvent,
                Math.Min(request.MaxBytes, server.PageBudget),
                request.MaxEvents);
        }

        /** 日志字节切片: 只是把范围交给后端按帧切字节, 不做任何物化/序列化。 */
        public Task<RemoteLogSlice> GetSessionLogSliceAsync(RemoteLogSliceRequest request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.Read);
            if (Backend.Session is not IRemoteLogSliceBackend slices)
                throw new InvalidOperationException($"session history is not served as bytes: {request.SessionId}");
            return slices.GetSessionLogSliceAsync(request.SessionId, request.FromEvent, request.MaxEvents, cancellationToken);
        }

        public async Task<IReadOnlyList<string>> ListToolsAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.Read);
            return await Backend.Session.ListToolsAsync(request.SessionId, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<RemoteSessionSummary>> ListRegistryAsync(CancellationToken cancellationToken)
        {
            Authorize(HostAction.Read);
            return Task.FromResult(Backend.Sessions.List());
        }

        public Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken)
        {
            Authorize(HostAction.Read);
            return Task.FromResult(Backend.Pending.List());
        }

        public Task<bool> RespondApprovalAsync(RemoteApprovalResponse request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.Approve);
            return Task.FromResult(Backend.Pending.RespondApproval(request, Resolver));
        }

        public Task<bool> RespondQuestionAsync(RemoteQuestionResponse request, CancellationToken cancellationToken)
        {
            Authorize(HostAction.Approve);
            return Task.FromResult(Backend.Pending.RespondQuestion(request, Resolver));
        }

        private Task StartPtyPumpAsync(IRemotePtyBackend backend, string subscriptionId, string ptyId)
            => _ptyPumps.StartAsync(subscriptionId, token => backend.AttachPtyAsync(ptyId, token),
                (output, token) => PumpPtyAsync(subscriptionId, output, token));

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
                if (backend.Mesh is { } mesh)
                    subscriptions.Add(await mesh.SubscribeAsync(lifetime).ConfigureAwait(false));
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
                    await PushAsync(notification).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        /** 广播通知按方法名落到推送契约的对应方法上。 */
        private Task PushAsync(HostNotification notification)
            => (notification.Method, _push) switch
            {
                (HostProtocol.NotificationPendingChanged, { } push) => push.PendingChangedAsync(Deserialize(notification, HostProtocolJsonContext.Default.RemotePendingChange)),
                (HostProtocol.NotificationSessionsChanged, { } push) => push.SessionsChangedAsync(Deserialize(notification, HostProtocolJsonContext.Default.RemoteSessionChange)),
                (NotificationsProtocol.NotificationNotifyChanged, { } push) => push.NotifyChangedAsync(Deserialize(notification, NotificationsProtocolJsonContext.Default.RemoteNotificationChange)),
                (MeshProtocol.NotificationMeshChanged, { } push) => PushMeshChangedSafelyAsync(push, Deserialize(notification, MeshProtocolJsonContext.Default.RemoteMeshChange)),
                _ => Task.CompletedTask,
            };

        private static async Task PushMeshChangedSafelyAsync(IHostPush push, RemoteMeshChange change)
        {
            try
            {
                await push.MeshChangedAsync(change).ConfigureAwait(false);
            }
            catch (RemoteInvocationException)
            {
                // Older clients do not implement host.mesh.changed; ignore method-not-found safely.
            }
        }

        private static T Deserialize<T>(HostNotification notification, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
            => notification.Params is { } payload
                ? System.Text.Json.JsonSerializer.Deserialize(payload, info)
                    ?? throw new IOException($"empty {notification.Method} notification payload")
                : throw new IOException($"empty {notification.Method} notification payload");

        private async Task PumpPtyAsync(string subscriptionId, IAsyncEnumerable<RemotePtyOutput> output, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var item in output.WithCancellation(cancellationToken).ConfigureAwait(false))
                    if (_push is { } push)
                        await push.PtyOutputAsync(new RemotePtyFrame(subscriptionId, item), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        private async Task PumpEventsAsync(string subscriptionId, IAsyncEnumerable<RemoteSessionEvent> events, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var item in events.WithCancellation(cancellationToken).ConfigureAwait(false))
                    if (_push is { } push)
                        await push.EventAsync(new RemoteEventFrame(subscriptionId, item), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
            }
        }

        /** 编辑器域的广播通知按方法名落到推送契约的对应方法上; 编辑器域目前没有独立推送帧。 */
        private Task PushToAsync(string method, System.Text.Json.JsonElement payload, CancellationToken cancellationToken)
            => PushAsync(new HostNotification(method, payload)).WaitAsync(cancellationToken);
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

    private sealed class HostMeshMethods(IRemoteMeshBackend backend, Action<HostAction> authorize, Func<HostMeshCaller> caller) : IMeshRpc
    {
        public Task<IReadOnlyList<AgentProfile>> ListAgentsAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return backend.ListAgentsAsync(cancellationToken);
        }

        public Task<AgentProfile> CreateAgentAsync(AgentProfileRegistration request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return backend.CreateAgentAsync(request, caller(), cancellationToken);
        }

        public Task<AgentProfile> WakeAgentAsync(RemoteMeshAgentRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return backend.WakeAgentAsync(request.AgentId, caller(), cancellationToken);
        }

        public Task<bool> SuspendAgentAsync(RemoteMeshAgentRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return backend.SuspendAgentAsync(request.AgentId, caller(), cancellationToken);
        }

        public Task<AgentDeliveryResult> SendToAgentAsync(RemoteMeshSendRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return backend.SendToAgentAsync(request, caller(), cancellationToken);
        }

        public Task<IReadOnlyList<RemoteExecutionTarget>> ListTargetsAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return backend.ListTargetsAsync(cancellationToken);
        }

        public Task<IReadOnlyList<CollaborationRoom>> ListRoomsAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return backend.ListRoomsAsync(caller(), cancellationToken);
        }

        public Task<CollaborationRoom> CreateRoomAsync(RemoteRoomCreateRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return backend.CreateRoomAsync(request, caller(), cancellationToken);
        }

        public Task<RoomInvite> CreateInviteAsync(RemoteRoomInviteRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Admin);
            return backend.CreateInviteAsync(request, caller(), cancellationToken);
        }

        public Task<CollaborationRoom> JoinRoomAsync(RemoteRoomJoinRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.SendMessage);
            return backend.JoinRoomAsync(request, caller(), cancellationToken);
        }

        public Task<RoomMessage> PostRoomMessageAsync(RemoteRoomPostRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.SendMessage);
            return backend.PostRoomMessageAsync(request, caller(), cancellationToken);
        }

        public Task<IReadOnlyList<RoomMessage>> ReadRoomMessagesAsync(RemoteRoomMessagesRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return backend.ReadRoomMessagesAsync(request, caller(), cancellationToken);
        }

        public Task<IReadOnlyList<DiscoveredAgentCard>> DiscoverAgentsAsync(RemoteDiscoveryQuery? request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return backend.DiscoverAgentsAsync(request, cancellationToken);
        }

        public Task<AgentTrustRecord> GrantTrustAsync(RemoteTrustGrantRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Admin);
            return backend.GrantTrustAsync(request, caller(), cancellationToken);
        }

        public Task<AgentTrustRecord> RevokeTrustAsync(RemoteTrustRevokeRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Admin);
            return backend.RevokeTrustAsync(request, caller(), cancellationToken);
        }

        public Task<IReadOnlyList<AgentTrustRecord>> ListTrustAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return backend.ListTrustAsync(cancellationToken);
        }

        public Task<IReadOnlyList<ChannelBinding>> ListChannelsAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return backend.ListChannelsAsync(cancellationToken);
        }

        public Task<ChannelBinding> BindChannelAsync(ChannelBinding request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Admin);
            return backend.BindChannelAsync(request, caller(), cancellationToken);
        }

        public Task<IReadOnlyList<AutomationTriggerRule>> ListTriggersAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return backend.ListTriggersAsync(cancellationToken);
        }
    }

    private sealed class RoleGuardedAgentRpc(IAgentRpc inner, Action<HostAction> authorize) : IAgentRpc
    {
        public Task<IReadOnlyList<RemoteAgentCatalogEntry>> CatalogAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.CatalogAsync(cancellationToken);
        }

        public Task<RemoteSessionSummary> StartAsync(RemoteAgentStartRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.StartAsync(request, cancellationToken);
        }

        public Task<bool> PromptAsync(RemoteAgentPrompt request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.PromptAsync(request, cancellationToken);
        }

        public Task<bool> CancelAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.CancelAsync(request, cancellationToken);
        }

        public Task<bool> StopAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.StopAsync(request, cancellationToken);
        }

        public Task<RemoteSessionSummary> ResumeAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.ResumeAsync(request, cancellationToken);
        }
    }

    private sealed class RoleGuardedPtyRpc(IPtyRpc inner, Action<HostAction> authorize) : IPtyRpc
    {
        public Task<RemotePtyRef> StartAsync(RemotePtyStartRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.StartAsync(request, cancellationToken);
        }

        public Task<bool> WriteAsync(RemotePtyInput request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.WriteAsync(request, cancellationToken);
        }

        public Task<bool> StopAsync(RemotePtyRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.StopAsync(request, cancellationToken);
        }

        public Task<bool> AttachAsync(RemotePtyAttachRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.AttachAsync(request, cancellationToken);
        }

        public Task<bool> DetachAsync(RemoteSubscriptionRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.DetachAsync(request, cancellationToken);
        }

        public Task<bool> ResizeAsync(RemotePtyResize request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.ResizeAsync(request, cancellationToken);
        }
    }

    private sealed class RoleGuardedFilesRpc(IFilesRpc inner, Action<HostAction> authorize) : IFilesRpc
    {
        public Task<RemoteFileContent> ReadFileAsync(RemoteFilePath request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.ReadFileAsync(request, cancellationToken);
        }

        public Task<bool> WriteFileAsync(RemoteFileContent request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.WriteFileAsync(request, cancellationToken);
        }

        public Task<RemoteDirectoryListing> ListDirectoryAsync(RemoteFilePath request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.ListDirectoryAsync(request, cancellationToken);
        }

        public Task<RemoteFileStat> StatAsync(RemoteFilePath request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.StatAsync(request, cancellationToken);
        }

        public Task<bool> CreateDirectoryAsync(RemoteFilePath request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.CreateDirectoryAsync(request, cancellationToken);
        }

        public Task<bool> RenameAsync(RemoteFsRename request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.RenameAsync(request, cancellationToken);
        }

        public Task<bool> DeleteAsync(RemoteFsDelete request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.DeleteAsync(request, cancellationToken);
        }

        public Task<RemoteUploadRef> BeginUploadAsync(RemoteUploadBegin request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.BeginUploadAsync(request, cancellationToken);
        }

        public Task<long> UploadChunkAsync(RemoteUploadChunk request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.UploadChunkAsync(request, cancellationToken);
        }

        public Task<RemoteFileStat> CommitUploadAsync(RemoteUploadCommit request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.CommitUploadAsync(request, cancellationToken);
        }

        public Task<bool> AbortUploadAsync(RemoteUploadRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.AbortUploadAsync(request, cancellationToken);
        }

        public Task<RemoteFileChunk> DownloadAsync(RemoteDownloadRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.DownloadAsync(request, cancellationToken);
        }
    }

    private sealed class RoleGuardedGitRpc(IGitRpc inner, Action<HostAction> authorize) : IGitRpc
    {
        public Task<GitStatus> StatusAsync(RemoteGitRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.StatusAsync(request, cancellationToken);
        }

        public Task<GitDiff> DiffAsync(RemoteGitDiffRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.DiffAsync(request, cancellationToken);
        }

        public Task<IReadOnlyList<GitCommitSummary>> LogAsync(RemoteGitLogRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.LogAsync(request, cancellationToken);
        }

        public Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(RemoteGitRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.ListWorktreesAsync(request, cancellationToken);
        }

        public Task<GitWorktree> AddWorktreeAsync(RemoteGitWorktreeAdd request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.AddWorktreeAsync(request, cancellationToken);
        }

        public Task<bool> RemoveWorktreeAsync(RemoteGitWorktreeRemove request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.RemoveWorktreeAsync(request, cancellationToken);
        }
    }

    private sealed class RoleGuardedNotificationsRpc(INotificationsRpc inner, Action<HostAction> authorize) : INotificationsRpc
    {
        public Task<RemoteNotifyPage> ListAsync(RemoteNotifyQuery? request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.ListAsync(request, cancellationToken);
        }

        public Task<int> MarkReadAsync(RemoteNotifyMarkRead request, CancellationToken cancellationToken)
        {
            authorize(HostAction.SendMessage);
            return inner.MarkReadAsync(request, cancellationToken);
        }
    }

    private sealed class RoleGuardedSessionEditingRpc(ISessionEditingRpc inner, Action<HostAction> authorize) : ISessionEditingRpc
    {
        public Task<RemoteCommandResult> ExecuteCommandAsync(RemoteCommandRequest request, CancellationToken cancellationToken)
        {
            var trimmed = request.Line.TrimStart();
            if (IsAdminSlashCommand(trimmed))
                authorize(HostAction.Admin);
            else
                authorize(HostAction.SendMessage);
            return inner.ExecuteCommandAsync(request, cancellationToken);
        }

        private static bool IsAdminSlashCommand(string trimmed)
        {
            if (!trimmed.StartsWith('/'))
                return false;
            var space = trimmed.IndexOf(' ');
            var command = space > 1 ? trimmed[..space] : trimmed;
            return command.Equals("/discover", StringComparison.OrdinalIgnoreCase)
                || command.Equals("/room", StringComparison.OrdinalIgnoreCase)
                || command.Equals("/channel", StringComparison.OrdinalIgnoreCase)
                || command.Equals("/plugins", StringComparison.OrdinalIgnoreCase)
                || command.Equals("/safety", StringComparison.OrdinalIgnoreCase)
                || command.Equals("/sandbox", StringComparison.OrdinalIgnoreCase);
        }

        public Task<IReadOnlyList<RemoteCommandDescriptor>> ListCommandsAsync(RemoteCommandRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.ListCommandsAsync(request, cancellationToken);
        }

        public Task<bool> RenameAsync(RemoteSessionRenameRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.RenameAsync(request, cancellationToken);
        }

        public Task<bool> DeleteAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.MutateSession);
            return inner.DeleteAsync(request, cancellationToken);
        }

        public Task<IReadOnlyList<RemoteTimelineEntry>> SessionTimelineAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.SessionTimelineAsync(request, cancellationToken);
        }

        public Task<bool> SetApprovalPolicyAsync(RemoteSessionPolicyRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Approve);
            return inner.SetApprovalPolicyAsync(request, cancellationToken);
        }

        public Task<IReadOnlyList<RemoteSubagentInfo>> ListSubagentsAsync(RemoteSessionRef request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.ListSubagentsAsync(request, cancellationToken);
        }
    }

    private sealed class RoleGuardedPluginsRpc(IPluginsRpc inner, Action<HostAction> authorize) : IPluginsRpc
    {
        public Task<IReadOnlyList<RemotePluginInfo>> ListAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.ListAsync(cancellationToken);
        }

        public Task<string> SetPermissionAsync(RemotePluginPermissionRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Admin);
            return inner.SetPermissionAsync(request, cancellationToken);
        }
    }

    private sealed class RoleGuardedNetworkRpc(HostNetworkMethods inner, Action<HostAction> authorize) : INetworkRpc, IAsyncDisposable
    {
        public Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.GetOutboundAsync(cancellationToken);
        }

        public Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Admin);
            return inner.SetOutboundAsync(request, cancellationToken);
        }

        public Task<RemoteOutboundProxyProbeResult> ProbeAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.ProbeAsync(request, cancellationToken);
        }

        public Task<bool> CancelProbeAsync(RemoteOutboundProxyProbeCancelRequest request, CancellationToken cancellationToken)
        {
            authorize(HostAction.Read);
            return inner.CancelProbeAsync(request, cancellationToken);
        }

        public Task<bool> ReloadAsync(CancellationToken cancellationToken)
        {
            authorize(HostAction.Admin);
            return inner.ReloadAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
