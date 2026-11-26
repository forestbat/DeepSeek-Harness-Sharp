using StreamJsonRpc;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Dsh.Core;
using Dsh.Ide;
using Dsh.Ide.Bridge;
using Dsh.RemoteHost.Rpc;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * 本地侧连接远端 host: 先握手(版本/token), 然后实现 RemoteHost 客户端。
 * 契约方法由 StreamJsonRpc 生成代理直接调用; 服务端推送落到本类实现的 IHostPush 上, 再转成本端的异步流。
 */
public sealed class RemoteHostClient : IAsyncDisposable, IWorkspaceHost, IHostPush
{
    private readonly JsonRpcChannel _channel;
    private readonly IHostRpc _rpc;
    private readonly RemotePtyClient _pty;
    private readonly RemoteNotificationsClient _notifications;
    private readonly ConcurrentDictionary<string, Channel<RemoteSessionEvent>> _eventStreams = new(StringComparer.Ordinal);
    private readonly Channel<RemotePendingChange> _pendingChanges = Channel.CreateUnbounded<RemotePendingChange>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<RemoteSessionChange> _sessionChanges = Channel.CreateUnbounded<RemoteSessionChange>(new UnboundedChannelOptions { SingleReader = true });

    /** 推送目标在握手之前就挂上: host 在 hello 应答之后立即补发未决请求, 晚挂会漏掉它们。 */
    private RemoteHostClient(JsonRpcChannel channel)
    {
        _channel = channel;
        _rpc = channel.Attach<IHostRpc>();
        Agent = new RemoteAgentClient(this, channel.Attach<IAgentRpc>());
        _pty = new RemotePtyClient(this, channel.Attach<IPtyRpc>());
        Files = new RemoteFilesClient(this, channel.Attach<IFilesRpc>());
        Git = new RemoteGitClient(this, channel.Attach<IGitRpc>());
        _notifications = new RemoteNotificationsClient(this, channel.Attach<INotificationsRpc>());
        SessionEditing = new RemoteSessionEditingClient(this, channel.Attach<ISessionEditingRpc>());
        Plugins = new RemotePluginsClient(this, channel.Attach<IPluginsRpc>());
        Network = new RemoteNetworkClient(this, channel.Attach<INetworkRpc>());
        Editor = new RemoteEditorClient(this, channel);
        _ = ObserveClosedAsync(channel.Completion);
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
        => Supports(EditorProtocol.Capability) ? Editor as T : null;
    IWorkspaceFiles IWorkspaceHost.Files => Files;
    IWorkspaceGit IWorkspaceHost.Git => Git;
    IWorkspaceNotifications IWorkspaceHost.Notifications => Notifications;
    IWorkspaceSessionEditing IWorkspaceHost.SessionEditing => SessionEditing;
    IWorkspacePlugins IWorkspaceHost.Plugins => Plugins;
    IWorkspaceNetwork IWorkspaceHost.Network => Network;
    IWorkspaceTerminals IWorkspaceHost.Pty => Pty;

    internal bool IsClosed => _closed.Task.IsCompleted;

    /** Whether the host advertises a capability from HostProtocolCatalog. */
    public bool Supports(string capability) => Hello.Capabilities?.Contains(capability) == true;

    public Task WhenClosed => _closed.Task;

    /**
     * 浏览器中继专用: 浏览器按方法名调用, 这里原样转给 host。
     * 契约方法一律走强类型代理, 这里只做"字符串方法名 -> 强类型实现"的翻译, 不做参数校验。
     */
    public Task<JsonElement> ForwardAsync(string method, JsonElement? parameters, CancellationToken cancellationToken = default)
    {
        RequireDecisionCancellationIfCancelling(method, parameters);
        return _channel.InvokeAsync(method, parameters, cancellationToken);
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
        RemoteHostClient? client = null;
        var target = new ClientTarget();
        var channel = JsonRpcChannel.Connect(duplex, HostRpcFormatter.CreateJson(EditorProtocolJsonContext.Default),
            [target], target.Methods());
        try
        {
            client = new RemoteHostClient(channel);
            target.Client = client;
            var hello = await client._rpc.HelloAsync(new HostHelloRequest(token, HostProtocol.Version, clientName), cancellationToken).ConfigureAwait(false);
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
            if (client is null)
                channel.Dispose();
            else
                await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<long> PingAsync(CancellationToken cancellationToken = default)
        => _rpc.PingAsync(cancellationToken);

    public Task<HostInfo> InfoAsync(CancellationToken cancellationToken = default)
        => _rpc.InfoAsync(cancellationToken);

    public Task<IReadOnlyList<RemoteSessionSummary>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => _rpc.ListSessionsAsync(cancellationToken);

    public Task<RemoteSessionSummary> CreateSessionAsync(string cwd, CancellationToken cancellationToken = default)
        => _rpc.CreateSessionAsync(new RemoteSessionCreateRequest(cwd), cancellationToken);

    public Task<RemoteSessionSummary> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _rpc.ResumeSessionAsync(new RemoteSessionRef(sessionId), cancellationToken);

    /** 整段历史从第一条开始取; 一页装不下由调用方按 TotalEventCount 续取。 */
    public Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, CancellationToken cancellationToken = default)
        => GetSessionSnapshotAsync(sessionId, tailCount: 0, cancellationToken);

    /**
     * tailCount > 0 时只要末尾这么多条。
     * 先发一个越界请求只为了拿到总条数, 再从 total - tailCount 起取, 于是手机只下载尾部这一截。
     */
    public async Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, int tailCount, CancellationToken cancellationToken = default)
    {
        var from = 0L;
        if (tailCount > 0)
        {
            var total = await TotalEventCountAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (total > tailCount)
                from = total - tailCount;
        }
        return await GetSessionSnapshotFromAsync(sessionId, from, cancellationToken).ConfigureAwait(false);
    }

    /**
     * 尾部 tailCount 条, 且单页最多 maxBytes 字节。
     * 手机端要的是一屏, 拿满 12 MB 一页再裁掉既慢又会让窗口内容超出预期。
     */
    public async Task<RemoteSessionSnapshot> GetSessionSnapshotAsync(string sessionId, int tailCount, int maxBytes, CancellationToken cancellationToken = default)
    {
        var from = 0L;
        if (tailCount > 0)
        {
            var total = await TotalEventCountAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (total > tailCount)
                from = total - tailCount;
        }
        return await GetSessionSnapshotFromAsync(sessionId, from, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /**
     * 从 fromEvent 起取一段(受单页字节预算约束, 需要更多时调用方自己续取)。
     * 手机按消息序号定位时用这个: 想看的消息在哪儿就从哪儿开始, 不必把前面的历史也拖下来。
     */
    public Task<RemoteSessionSnapshot> GetSessionSnapshotFromAsync(string sessionId, long fromEvent, CancellationToken cancellationToken = default)
        => FetchSnapshotPageAsync(sessionId, fromEvent, HostProtocol.SnapshotPageBytes, cancellationToken);

    /**
     * 从 fromEvent 起最多取 maxBytes 字节。
     * 服务端默认一页是 12 MB, 一页就能装下几千条事件; 手机只要一屏,
     * 拿满一页再在本地裁掉, 等于白下载白渲染 —— 这也是"上滑取更早"要等很久却看不出变化的原因。
     */
    public Task<RemoteSessionSnapshot> GetSessionSnapshotFromAsync(string sessionId, long fromEvent, int maxBytes, CancellationToken cancellationToken = default)
        => FetchSnapshotPageAsync(sessionId, fromEvent, maxBytes, cancellationToken);

    /**
     * 从 fromEvent 起最多取 maxEvents 条(同时受单页字节预算约束)。
     * 按条数要, 而不是按字节: 手机只要一屏, 字节预算会塞进上千条, 传回来立刻被丢掉。
     */
    public Task<RemoteSessionSnapshot> GetSessionSnapshotRangeAsync(string sessionId, long fromEvent, int maxEvents, CancellationToken cancellationToken = default)
        => _rpc.GetSessionSnapshotAsync(new RemoteSessionSnapshotRequest(sessionId, fromEvent, HostProtocol.SnapshotPageBytes, maxEvents), cancellationToken);

    /**
     * 一段**日志字节**: 宿主只按帧切磁盘上的字节, 不解压不序列化。
     *
     * 客户端拿回去自己解析(与读日志同一套代码), 于是线缆体积等于日志压缩体积,
     * 宿主也不必为每个读者物化一遍事件。MaxEvents 只决定一段带多少条, 是往返单价。
     */
    public Task<RemoteLogSlice> GetSessionLogSliceAsync(string sessionId, long fromEvent, int maxEvents, CancellationToken cancellationToken = default)
        => _rpc.GetSessionLogSliceAsync(new RemoteLogSliceRequest(sessionId, fromEvent, maxEvents), cancellationToken);

    /** 整段历史有多少条; 用一个越界请求问, 服务端只回总数不回事件。 */
    public async Task<long> TotalEventCountAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var probe = await FetchSnapshotPageAsync(sessionId, long.MaxValue, HostProtocol.SnapshotPageBytes, cancellationToken).ConfigureAwait(false);
        return probe.TotalEventCount;
    }

    private Task<RemoteSessionSnapshot> FetchSnapshotPageAsync(string sessionId, long fromEvent, int maxBytes, CancellationToken cancellationToken)
        => _rpc.GetSessionSnapshotAsync(new RemoteSessionSnapshotRequest(sessionId, fromEvent, maxBytes), cancellationToken);

    public async IAsyncEnumerable<RemoteSessionEvent> SubscribeAsync(string sessionId, long fromSeq, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var subscriptionId = Guid.NewGuid().ToString("N");
        var channel = Channel.CreateUnbounded<RemoteSessionEvent>(new UnboundedChannelOptions { SingleReader = true });
        _eventStreams[subscriptionId] = channel;
        try
        {
            await _rpc.SubscribeAsync(new RemoteSubscribeRequest(sessionId, fromSeq, subscriptionId), cancellationToken).ConfigureAwait(false);
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            _eventStreams.TryRemove(subscriptionId, out _);
            await StopStreamAsync(subscriptionId).ConfigureAwait(false);
        }
    }

    public Task SendMessageAsync(string sessionId, string text, IReadOnlyList<RemoteImageBlock> images, CancellationToken cancellationToken = default)
        => _rpc.SendMessageAsync(new RemoteMessageRequest(sessionId, text, images), cancellationToken);

    public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default)
        => _rpc.InterruptAsync(new RemoteSessionRef(sessionId), cancellationToken);

    public Task<IReadOnlyList<string>> ListToolsAsync(string sessionId, CancellationToken cancellationToken = default)
        => _rpc.ListToolsAsync(new RemoteSessionRef(sessionId), cancellationToken);

    public IAsyncEnumerable<RemotePendingChange> PendingChangesAsync(CancellationToken cancellationToken = default)
        => _pendingChanges.Reader.ReadAllAsync(cancellationToken);

    public Task<bool> RespondApprovalAsync(string requestId, bool allow, string? reason = null, CancellationToken cancellationToken = default, bool rememberForSession = false, bool cancelled = false)
    {
        if (cancelled)
            RequireDecisionCancellation();
        return _rpc.RespondApprovalAsync(new RemoteApprovalResponse(requestId, allow, reason, rememberForSession, cancelled), cancellationToken);
    }

    public Task<bool> RespondQuestionAsync(string requestId, IReadOnlyList<RemoteQuestionAnswerItem> answers, CancellationToken cancellationToken = default, string status = "answered", string? reason = null)
    {
        if (status == "dismissed")
            RequireDecisionCancellation();
        return _rpc.RespondQuestionAsync(new RemoteQuestionResponse(requestId, answers, status, reason), cancellationToken);
    }

    /** 撤回类结果只有声明了决策取消能力的 host 才认得; 提前拒掉, 别把在途决策弄成半截。 */
    private void RequireDecisionCancellationIfCancelling(string method, JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value)
            return;
        var cancelling = (method == HostProtocol.MethodApprovalRespond
                && value.TryGetProperty(nameof(RemoteApprovalResponse.Cancelled), out var cancelled)
                && cancelled.ValueKind == JsonValueKind.True)
            || (method == HostProtocol.MethodQuestionRespond
                && value.TryGetProperty(nameof(RemoteQuestionResponse.Status), out var status)
                && status.ValueKind == JsonValueKind.String && status.ValueEquals("dismissed"));
        if (cancelling)
            RequireDecisionCancellation();
    }

    private void RequireDecisionCancellation()
    {
        if (!Supports(DecisionProtocol.Capability))
            throw new NotSupportedException($"Unsupported: host does not support {DecisionProtocol.Capability}; the request is still pending. Upgrade the host to cancel it.");
    }

    public Task<RemotePendingList> ListPendingAsync(CancellationToken cancellationToken = default)
        => _rpc.ListPendingAsync(cancellationToken);

    public Task<IReadOnlyList<RemoteSessionSummary>> ListHostSessionsAsync(CancellationToken cancellationToken = default)
        => _rpc.ListRegistryAsync(cancellationToken);

    public IAsyncEnumerable<RemoteSessionChange> HostSessionChangesAsync(CancellationToken cancellationToken = default)
        => _sessionChanges.Reader.ReadAllAsync(cancellationToken);

    private async Task StopStreamAsync(string subscriptionId)
    {
        if (IsClosed)
            return;
        try
        {
            await _rpc.UnsubscribeAsync(new RemoteSubscriptionRef(subscriptionId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or ConnectionLostException)
        {
        }
    }

    /** 服务端推送: 会话事件交给订阅流, 审批/会话变化交给各自的通道。 */
    public Task EventAsync(RemoteEventFrame frame, CancellationToken cancellationToken = default)
    {
        if (_eventStreams.TryGetValue(frame.SubscriptionId, out var events))
            events.Writer.TryWrite(frame.Event);
        return Task.CompletedTask;
    }

    public Task PendingChangedAsync(RemotePendingChange change, CancellationToken cancellationToken = default)
    {
        _pendingChanges.Writer.TryWrite(change);
        return Task.CompletedTask;
    }

    public Task SessionsChangedAsync(RemoteSessionChange change, CancellationToken cancellationToken = default)
    {
        _sessionChanges.Writer.TryWrite(change);
        return Task.CompletedTask;
    }

    public Task NotifyChangedAsync(RemoteNotificationChange change, CancellationToken cancellationToken = default)
        => _notifications.OnChangedAsync(change);

    public Task PtyOutputAsync(RemotePtyFrame frame, CancellationToken cancellationToken = default)
        => _pty.OnOutputAsync(frame);

    /** 反向调用: host 请本端打开/取消编辑器。 */
    public Task<EditorResult> ClientOpenAsync(RemoteEditorOpen request, CancellationToken cancellationToken = default)
        => Editor.ClientOpenAsync(request, cancellationToken);

    public Task<bool> ClientCancelAsync(RemoteEditorCancel request, CancellationToken cancellationToken = default)
        => Editor.ClientCancelAsync(request, cancellationToken);

    private async Task ObserveClosedAsync(Task completion)
    {
        Exception? failure = null;
        try
        {
            await completion.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        OnClosed(failure);
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

    /** 逐次请求的分段耗时; 只用于诊断, 平时为 null。 */
    public static Action<string>? HandshakeTrace { get; set; }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await Editor.DisposeAsync().ConfigureAwait(false);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    /** 连接建立时客户端还不存在, 推送目标要延迟到握手之后才能取。 */
    private sealed class ClientTarget : IHostPush
    {
        private readonly TaskCompletionSource<RemoteHostClient> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /** 握手前后到达的推送都要落地: 没就绪就等, 不能丢。 */
        public RemoteHostClient? Client
        {
            set
            {
                if (value is not null)
                    _ready.TrySetResult(value);
            }
        }

        private async Task<RemoteHostClient> OwnerAsync() => await _ready.Task.ConfigureAwait(false);

        public async Task EventAsync(RemoteEventFrame frame, CancellationToken cancellationToken = default)
            => await (await OwnerAsync().ConfigureAwait(false)).EventAsync(frame, cancellationToken).ConfigureAwait(false);

        public async Task PendingChangedAsync(RemotePendingChange change, CancellationToken cancellationToken = default)
            => await (await OwnerAsync().ConfigureAwait(false)).PendingChangedAsync(change, cancellationToken).ConfigureAwait(false);

        public async Task SessionsChangedAsync(RemoteSessionChange change, CancellationToken cancellationToken = default)
            => await (await OwnerAsync().ConfigureAwait(false)).SessionsChangedAsync(change, cancellationToken).ConfigureAwait(false);

        public async Task NotifyChangedAsync(RemoteNotificationChange change, CancellationToken cancellationToken = default)
            => await (await OwnerAsync().ConfigureAwait(false)).NotifyChangedAsync(change, cancellationToken).ConfigureAwait(false);

        public async Task PtyOutputAsync(RemotePtyFrame frame, CancellationToken cancellationToken = default)
            => await (await OwnerAsync().ConfigureAwait(false)).PtyOutputAsync(frame, cancellationToken).ConfigureAwait(false);

        /** client.ide.* 两条反向方法按线协议名登记。 */
        public (string Method, Delegate Handler)[] Methods() =>
        [
            (EditorProtocol.MethodClientOpen, (Func<RemoteEditorOpen, CancellationToken, Task<EditorResult>>)ClientOpenAsync),
            (EditorProtocol.MethodClientCancel, (Func<RemoteEditorCancel, CancellationToken, Task<bool>>)ClientCancelAsync),
        ];

        public async Task<EditorResult> ClientOpenAsync(RemoteEditorOpen request, CancellationToken cancellationToken = default)
            => await (await OwnerAsync().ConfigureAwait(false)).ClientOpenAsync(request, cancellationToken).ConfigureAwait(false);

        public async Task<bool> ClientCancelAsync(RemoteEditorCancel request, CancellationToken cancellationToken = default)
            => await (await OwnerAsync().ConfigureAwait(false)).ClientCancelAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
