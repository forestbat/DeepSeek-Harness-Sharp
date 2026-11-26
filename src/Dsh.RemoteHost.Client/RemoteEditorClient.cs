using StreamJsonRpc;
using Dsh.Ide;
using Dsh.Ide.Bridge;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/// <summary>Independent IDE domain plus the local half of the typed, cancellable extension protocol.</summary>
public sealed class RemoteEditorClient(RemoteHostClient host, JsonRpcChannel rpc) : IAsyncDisposable, IRemoteEditors
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly EditorBridgeOperations _incoming = new(CancellationToken.None);
    private IEditorService? _local;
    private Func<EditorRequest, EditorRequest>? _map;
    private bool _registered;
    private Task? _retirement;
    private bool _disposed;

    public Task<EditorRemoteList> ListAsync(CancellationToken cancellationToken = default, bool refresh = false)
        => rpc.InvokeAsync<EditorRemoteList>(EditorProtocol.MethodList, cancellationToken, new RemoteEditorList(refresh));

    public Task<EditorRemoteStatus> StatusAsync(string workspaceRoot, CancellationToken cancellationToken = default, string? sessionId = null)
        => rpc.InvokeAsync<EditorRemoteStatus>(EditorProtocol.MethodStatus, cancellationToken, new RemoteEditorWorkspace(workspaceRoot, sessionId));

    public Task<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        => rpc.InvokeAsync<IReadOnlyList<EditorDiagnostic>>(EditorProtocol.MethodDoctor, cancellationToken, new RemoteEditorWorkspace(workspaceRoot));

    public async Task<EditorResult> OpenAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        try
        {
            // 取消走显式的 host.ide.cancel, 不依赖 $/cancelRequest: 打开动作在 host 侧还牵着反向调用和本端工作,
            // 必须由 host 主动收尾, 否则本地启动器会一直挂着。
            return await rpc.InvokeAsync<EditorResult>(EditorProtocol.MethodOpen, CancellationToken.None, new RemoteEditorOpen(id, request)).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!host.IsClosed)
            {
                try { await CancelAsync(id).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            throw;
        }
    }

    private async Task<bool> CancelAsync(Guid id)
        => await rpc.InvokeAsync<bool>(EditorProtocol.MethodCancel, _lifetime.Token, new RemoteEditorCancel(id)).ConfigureAwait(false);

    /// <summary>The mapping is trusted local configuration; absence never treats remote filesystem paths as local.</summary>
    public async Task<IAsyncDisposable> RegisterClientAsync(IEditorService localService, CancellationToken cancellationToken = default, Func<EditorRequest, EditorRequest>? map = null, bool isLocalMachine = false, bool hasMappings = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registered) throw new InvalidOperationException("An IDE handler is already registered on this connection.");
            _registered = true;
            _local = localService;
            _map = map;
        }
        try
        {
            await rpc.InvokeAsync<bool>(EditorProtocol.MethodRegister, cancellationToken, new RemoteEditorClientRegistration(IsLocalMachine: isLocalMachine, HasMappings: hasMappings)).ConfigureAwait(false);
            return new Registration(this);
        }
        catch
        {
            lock (_gate) { _registered = false; _local = null; _map = null; }
            if (!host.IsClosed)
            {
                try { await UnregisterAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            throw;
        }
    }

    public Task<bool> SelectForSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => rpc.InvokeAsync<bool>(EditorProtocol.MethodSelect, cancellationToken, new RemoteEditorSelect(sessionId));

    /** 反向调用 client.ide.open: host 请本端按本地映射打开文件。 */
    internal async Task<EditorResult> ClientOpenAsync(RemoteEditorOpen request, CancellationToken cancellationToken)
    {
        if (!host.Hello.Ok)
            throw new JsonRpcException(-32602, "client.ide.open requires a typed request.");
        // host 侧的 $/cancelRequest 落到这条反向调用上, 要转成本地操作的取消, 否则本地工作不会停。
        using var registration = cancellationToken.Register(() => _ = _incoming.CancelAsync(request.OperationId));
        return await _incoming.Start(request.OperationId, token => LaunchLocalAsync(request.Request, token, cancellationToken)).ConfigureAwait(false);
    }

    /** 反向调用 client.ide.cancel。 */
    internal async Task<bool> ClientCancelAsync(RemoteEditorCancel request, CancellationToken cancellationToken)
    {
        await _incoming.CancelAsync(request.OperationId).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<EditorResult> LaunchLocalAsync(EditorRequest request, CancellationToken cancellationToken, CancellationToken connectionClosed)
    {
        IEditorService? local;
        Func<EditorRequest, EditorRequest>? map;
        lock (_gate) { local = _local; map = _map; }
        if (local is null)
            return new EditorResult { Error = EditorErrorCode.CLIENT_UNAVAILABLE, Message = "No local IDE handler is registered." };
        if (map is null)
            return new EditorResult { Error = EditorErrorCode.UNSUPPORTED, Message = "A locally trusted remote workspace mapping is required." };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionClosed, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            var mapped = map(request);
            // A remote request cannot select a locally detected host editor or redirect execution again.
            mapped = mapped with { ExecutionTarget = EditorExecutionTarget.Local, HostEditor = null, RememberSelection = false, MachineId = null };
            return await local.LaunchAsync(mapped, linked.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return new EditorResult { Error = EditorErrorCode.OUTSIDE_WORKSPACE, Message = "The request does not match a trusted workspace mapping." };
        }
        catch (NotSupportedException)
        {
            return new EditorResult { Error = EditorErrorCode.UNSUPPORTED, Message = "The selected editor does not support this remote mapping." };
        }
    }

    /**
     * 告诉对端"本连接不再提供编辑器"。
     *
     * 注册句柄的释放与客户端退役是两条独立的路径, 顺序不定: 连接先关闭时客户端的 CTS 已经释放,
     * 再去取 _lifetime.Token 会抛 ObjectDisposedException。既然连接都没了, 对端也不需要这条通知 ——
     * 已经退役就直接返回, 并在锁内取 token, 免得取到一半被释放。
     */
    private async ValueTask UnregisterAsync()
    {
        CancellationToken token;
        lock (_gate)
        {
            _registered = false;
            _local = null;
            _map = null;
            if (_disposed)
                return;
            token = _lifetime.Token;
        }
        if (host.IsClosed)
            return;
        await rpc.InvokeAsync<bool>(EditorProtocol.MethodUnregister, token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_retirement ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _registered = false;
            _local = null;
            _map = null;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _incoming.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed class Registration(RemoteEditorClient owner) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.UnregisterAsync() : ValueTask.CompletedTask;
    }
}
