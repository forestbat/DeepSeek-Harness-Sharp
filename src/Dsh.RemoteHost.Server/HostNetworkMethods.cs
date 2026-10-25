using System.Text.Json;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

internal sealed class HostNetworkMethods(IRemoteNetworkBackend backend, CancellationToken lifetime) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ProbeOperation> _probes = [];
    private bool _disposed;

    /** Register probe ownership and initiate cancellation in frame-receive order. */
    public ValueTask<JsonElement?>? Prepare(string name, JsonRpcMessage message)
    {
        switch (name)
        {
            case NetworkProtocol.MethodOutboundProbe:
            {
                var request = Require(message, NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeRequest, "outbound.probe requires a request");
                var operationId = request.OperationId ?? Guid.NewGuid();
                ProbeOperation operation;
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (_probes.ContainsKey(operationId))
                        throw new JsonRpcException(InvalidParams, "outbound.probe operationId is already active on this connection");
                    operation = new ProbeOperation(lifetime);
                    _probes.Add(operationId, operation);
                }
                return ProbeAsync(operationId, operation, request);
            }
            case NetworkProtocol.MethodOutboundProbeCancel:
            {
                var request = Require(message, NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeCancelRequest, "outbound.probe.cancel requires operationId");
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    if (!_probes.TryGetValue(request.OperationId, out var operation))
                        return ValueTask.FromResult<JsonElement?>(True);
                    return AcknowledgeCancellationAsync(operation, operation.Cancel());
                }
            }
            default:
                return null;
        }
    }

    public async ValueTask<JsonElement?> HandleAsync(string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case NetworkProtocol.MethodOutboundGet:
                return Serialize(await backend.GetOutboundAsync(cancellationToken).ConfigureAwait(false), NetworkProtocolJsonContext.Default.RemoteOutboundProxyState);
            case NetworkProtocol.MethodOutboundSet:
            {
                const string error = "outbound.set requires expectedRevision and settings";
                var request = Require(message, NetworkProtocolJsonContext.Default.RemoteOutboundProxySetRequest, error);
                if (request.Settings is null || string.IsNullOrWhiteSpace(request.ExpectedRevision))
                    throw new JsonRpcException(InvalidParams, error);
                return Serialize(await backend.SetOutboundAsync(request, cancellationToken).ConfigureAwait(false), NetworkProtocolJsonContext.Default.RemoteOutboundProxyState);
            }
            default:
                throw MethodNotFound(name);
        }
    }

    private async ValueTask<JsonElement?> ProbeAsync(Guid operationId, ProbeOperation operation, RemoteOutboundProxyProbeRequest request)
    {
        await Task.Yield();
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            return Serialize(await backend.ProbeOutboundAsync(request, operation.Token).ConfigureAwait(false), NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeResult);
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

    private static async ValueTask<JsonElement?> AcknowledgeCancellationAsync(ProbeOperation operation, Task callbacks)
    {
        await WaitForCancellationAsync(operation, callbacks).ConfigureAwait(false);
        return True;
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
        private Task? _cancellation;
        private bool _finishing;

        /** Link connection cancellation through the tracked task so callback completion remains observable. */
        public ProbeOperation(CancellationToken lifetime)
        {
            _lifetimeCancellation = lifetime.UnsafeRegister(static state =>
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
                _source.Dispose();
            }
        }
    }

    public static async ValueTask<JsonElement?> ReloadAsync(IRemoteNetworkReloadBackend backend, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        if (message.Params is { ValueKind: not JsonValueKind.Null })
            throw new JsonRpcException(InvalidParams, "network.reload takes no configuration parameters");
        await backend.ReloadAsync(cancellationToken).ConfigureAwait(false);
        return Serialize(true, HostProtocolJsonContext.Default.Boolean);
    }
}
