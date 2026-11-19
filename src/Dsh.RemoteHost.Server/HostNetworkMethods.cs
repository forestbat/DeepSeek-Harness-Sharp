using Dsh.RemoteHost.Rpc;
using Dsh.Transport;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

namespace Dsh.RemoteHost;

/**
 * host.network.* requests. 探测请求在开始时登记归属, 取消请求据此找到在途探测;
 * 连接结束时先取消全部在途探测并等它们的回调跑完, 才让连接关闭。
 */
internal sealed class HostNetworkMethods(IRemoteNetworkBackend backend,
    Action ensureAuthenticated, IRemoteNetworkReloadBackend? reload, CancellationToken lifetime)
    : INetworkRpc, IAsyncDisposable
{
    private const string SetError = "outbound.set requires expectedRevision and settings";

    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProbeOperation> _probes = [];
    private bool _disposed;

    public async Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        return await backend.GetOutboundAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        if (request.Settings is null || string.IsNullOrWhiteSpace(request.ExpectedRevision))
            throw new LocalRpcException(SetError) { ErrorCode = JsonRpcException.InvalidParams };
        try { return await backend.SetOutboundAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (JsonRpcException error)
        {
            throw new LocalRpcException(error.Message) { ErrorCode = error.Code, ErrorData = error.ErrorData };
        }
    }

    public async Task<RemoteOutboundProxyProbeResult> ProbeAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        var operationId = request.OperationId ?? Guid.NewGuid();
        ProbeOperation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_probes.ContainsKey(operationId))
                throw new LocalRpcException("outbound.probe operationId is already active on this connection") { ErrorCode = JsonRpcException.InvalidParams };
            operation = new ProbeOperation(lifetime, cancellationToken);
            _probes.Add(operationId, operation);
        }

        try
        {
            operation.Token.ThrowIfCancellationRequested();
            return await backend.ProbeOutboundAsync(request, operation.Token).ConfigureAwait(false);
        }
        finally
        {
            Exception? cleanupFailure = null;
            try
            {
                await operation.FinishAsync().ConfigureAwait(false);
            }
            catch (Exception error)
            {
                cleanupFailure = error;
                throw;
            }
            finally
            {
                lock (_gate)
                    _probes.Remove(operationId);
                if (cleanupFailure is null)
                    operation.Completion.TrySetResult();
                else
                    operation.Completion.TrySetException(cleanupFailure);
            }
        }
    }

    public async Task<bool> CancelProbeAsync(RemoteOutboundProxyProbeCancelRequest request, CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        ProbeOperation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_probes.TryGetValue(request.OperationId, out var found))
                return true;
            operation = found;
        }
        await WaitForCancellationAsync(operation, operation.Cancel()).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        ensureAuthenticated();
        // 声明了 network.reload 的 host 才有这个能力; 没实现的 host 对这个方法名就当它不存在。
        if (reload is null)
            throw new LocalRpcException("network.reload is not supported by this host") { ErrorCode = (int)JsonRpcErrorCode.MethodNotFound };
        await reload.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task WaitForCancellationAsync(ProbeOperation operation, Task callbacks)
    {
        try
        {
            await callbacks.ConfigureAwait(false);
        }
        finally
        {
            await operation.Completion.Task.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ensureAuthenticated();
        List<Task> pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            pending = new List<Task>(_probes.Count);
            foreach (var operation in _probes.Values)
                pending.Add(WaitForCancellationAsync(operation, operation.Cancel()));
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private sealed class ProbeOperation
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _source = new();
        private readonly CancellationTokenRegistration _lifetimeCancellation;
        private readonly CancellationTokenRegistration _requestCancellation;
        private Task? _cancellation;
        private bool _finishing;

        /** Link request and connection cancellation through the tracked task so callback completion remains observable. */
        public ProbeOperation(CancellationToken lifetime, CancellationToken request)
        {
            _lifetimeCancellation = lifetime.UnsafeRegister(static state =>
            {
                _ = ((ProbeOperation)state!).Cancel();
            }, this);
            _requestCancellation = request.UnsafeRegister(static state =>
            {
                _ = ((ProbeOperation)state!).Cancel();
            }, this);
        }

        public CancellationToken Token => _source.Token;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Cancel()
        {
            lock (_gate)
            {
                if (!_finishing)
                    return _cancellation ??= _source.CancelAsync();
                return _cancellation ?? Task.CompletedTask;
            }
        }

        public async Task FinishAsync()
        {
            Task callbacks;
            lock (_gate)
            {
                _finishing = true;
                callbacks = _cancellation ?? Task.CompletedTask;
            }
            try
            {
                await callbacks.ConfigureAwait(false);
            }
            finally
            {
                _lifetimeCancellation.Dispose();
                _requestCancellation.Dispose();
                _source.Dispose();
            }
        }
    }
}
