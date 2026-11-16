using Dsh.Core;
using Dsh.Ide;
using Dsh.Ide.Bridge;
using Dsh.RemoteHost.Rpc;
using Dsh.Transport;
using StreamJsonRpc;

namespace Dsh.RemoteHost;

/**
 * 编辑器域: 把 host 侧的打开/取消反向转给客户端。
 * 连接断开时反向调用自然失败, 所以不再单独维护"是否已关闭"的判断。
 */
internal sealed class HostEditorMethods(
    IRemoteEditorBackend backend,
    Action ensureAuthenticated,
    string clientId,
    CancellationToken lifetime) : IAsyncDisposable
{
    private readonly EditorBridgeOperations _operations = new(lifetime);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TaskCompletionSource> _reverseRequests = [];
    /** 反向调用(client.ide.*)走同一条通道, 不再为此保留一个只实现一次的接口。 */
    private JsonRpcChannel Channel => _channel ?? throw new LocalRpcException("IDE client channel is not ready") { ErrorCode = JsonRpcException.InvalidParams };
    private JsonRpcChannel? _channel;
    internal void AttachChannel(JsonRpcChannel channel) => _channel = channel;

    /** 八条 editor/* 方法按线协议名登记, 不为此定义只实现一次的接口。 */
    internal (string Method, Delegate Handler)[] Methods() =>
    [
        (EditorProtocol.MethodList, (Func<RemoteEditorList?, CancellationToken, Task<EditorRemoteList>>)ListAsync),
        (EditorProtocol.MethodStatus, (Func<RemoteEditorWorkspace, CancellationToken, Task<EditorRemoteStatus>>)StatusAsync),
        (EditorProtocol.MethodDoctor, (Func<RemoteEditorWorkspace, CancellationToken, Task<IReadOnlyList<EditorDiagnostic>>>)DoctorAsync),
        (EditorProtocol.MethodOpen, (Func<RemoteEditorOpen, CancellationToken, Task<EditorResult>>)OpenAsync),
        (EditorProtocol.MethodCancel, (Func<RemoteEditorCancel, CancellationToken, Task<bool>>)CancelAsync),
        (EditorProtocol.MethodRegister, (Func<RemoteEditorClientRegistration, CancellationToken, Task<bool>>)RegisterAsync),
        (EditorProtocol.MethodSelect, (Func<RemoteEditorSelect, CancellationToken, Task<bool>>)SelectAsync),
        (EditorProtocol.MethodUnregister, (Func<CancellationToken, Task<bool>>)UnregisterAsync),
    ];
    private IDisposable? _registration;
    private bool _disposed;

    public async Task<EditorRemoteList> ListAsync(RemoteEditorList? request, CancellationToken cancellationToken)
        => new(backend.Service.Catalog.Descriptors,
            await backend.Service.Catalog.DetectAsync(request?.Refresh ?? false, cancellationToken).ConfigureAwait(false));

    public Task<EditorRemoteStatus> StatusAsync(RemoteEditorWorkspace request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        var root = backend.Paths?.Resolve(request.WorkspaceRoot) ?? request.WorkspaceRoot;
        return Task.FromResult(new EditorRemoteStatus(
            backend.Service.InspectWorkspace(root),
            backend.Service.GetBinding(root, request.SessionId),
            backend.Service.LastDecision,
            backend.Clients?.Clients ?? []));
    }

    public async Task<IReadOnlyList<EditorDiagnostic>> DoctorAsync(RemoteEditorWorkspace request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        var root = backend.Paths?.Resolve(request.WorkspaceRoot) ?? request.WorkspaceRoot;
        return await backend.Service.DoctorAsync(root, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EditorResult> OpenAsync(RemoteEditorOpen request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        using var clientScope = backend.Clients?.EnterClientScope(clientId);
        Validate(request.Request);
        // 调用方的 $/cancelRequest 直接中止这次打开; 旧实现靠帧级预登记拿到同样的语义。
        return await _operations.Start(request.OperationId, async ct =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, cancellationToken);
            return await backend.Service.LaunchAsync(request.Request, linked.Token).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public async Task<bool> CancelAsync(RemoteEditorCancel request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        using var clientScope = backend.Clients?.EnterClientScope(clientId);
        await _operations.CancelAsync(request.OperationId).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RegisterAsync(RemoteEditorClientRegistration request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        using var clientScope = backend.Clients?.EnterClientScope(clientId);
        if (request.ProtocolVersion != EditorProtocol.Version)
            throw new LocalRpcException("IDE extension protocol version mismatch.") { ErrorCode = JsonRpcException.InvalidParams };
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (backend.Clients is not { } clients)
                throw new LocalRpcException("this host serves no IDE clients") { ErrorCode = JsonRpcException.InvalidParams };
            _registration ??= clients.RegisterClient(clientId, OpenClientAsync, lifetime, request.IsLocalMachine, request.HasMappings);
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UnregisterAsync(CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        using var clientScope = backend.Clients?.EnterClientScope(clientId);
        await RetireClientAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<bool> SelectAsync(RemoteEditorSelect request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        using var clientScope = backend.Clients?.EnterClientScope(clientId);
        lock (_gate)
        {
            if (_registration is null || backend.Clients is null)
                throw new LocalRpcException("Register this connection's IDE handler before selecting it.") { ErrorCode = JsonRpcException.InvalidParams };
            backend.Clients.SelectClient(request.SessionId, clientId);
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return true;
    }

    private async ValueTask<EditorResult> OpenClientAsync(EditorRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reverseRequests.Add(id, retired);
        }
        try
        {
            // 取消由本端显式发 client.ide.cancel: 反向调用要等客户端把本地启动器停掉才算收尾。
            return await Channel.InvokeAsync<EditorResult>(EditorProtocol.MethodClientOpen, CancellationToken.None, new RemoteEditorOpen(id, request))
                .WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await Channel.InvokeAsync<bool>(EditorProtocol.MethodClientCancel, lifetime, new RemoteEditorCancel(id)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or ConnectionLostException) { }
            }
            throw;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or ConnectionLostException)
        {
            return new EditorResult { Error = EditorErrorCode.CLIENT_UNAVAILABLE, Message = "The selected IDE client connection closed." };
        }
        finally
        {
            lock (_gate)
            {
                retired.TrySetResult();
                _reverseRequests.Remove(id);
            }
        }
    }

    private void Validate(EditorRequest request)
    {
        if (backend.Paths is not { } paths) return;
        var root = paths.Resolve(request.WorkspaceRoot);
        foreach (var path in new[] { request.Path, request.Left, request.Right })
            if (path is not null)
                paths.Resolve(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }

    private async Task RetireClientAsync()
    {
        IDisposable? registration;
        lock (_gate) { registration = _registration; _registration = null; }
        registration?.Dispose();
        Task[] pending;
        lock (_gate) pending = _reverseRequests.Values.Select(operation => operation.Task).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await RetireClientAsync().ConfigureAwait(false);
        await _operations.DisposeAsync().ConfigureAwait(false);
    }
}
