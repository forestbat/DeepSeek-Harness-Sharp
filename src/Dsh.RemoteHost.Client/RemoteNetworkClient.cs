using Dsh.RemoteHost.Rpc;
using Dsh.Transport;
using StreamJsonRpc;
using System.Text.Json;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Network domain. */
public sealed class RemoteNetworkClient : IWorkspaceNetwork
{
    private const int ProbeCancellationAcknowledgementTimeoutSeconds = 5;
    private readonly RemoteHostClient _host;
    private readonly INetworkRpc _rpc;

    internal RemoteNetworkClient(RemoteHostClient host, INetworkRpc rpc)
    {
        _host = host;
        _rpc = rpc;
    }

    public Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default)
        => InvokeAsync(() => _rpc.GetOutboundAsync(cancellationToken));

    public Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default)
        => InvokeAsync(() => _rpc.SetOutboundAsync(request, cancellationToken));

    public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_host.Supports(NetworkProtocol.CancellableProbeCapability))
            return await InvokeAsync(() => _rpc.ProbeAsync(request, cancellationToken)).ConfigureAwait(false);

        if (request.OperationId is null)
            request = request with { OperationId = Guid.NewGuid() };
        var pending = InvokeAsync(() => _rpc.ProbeAsync(request, cancellationToken));
        try
        {
            var result = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ConfirmProbeCleanupAsync(request.OperationId.Value, pending).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ConfirmProbeCleanupAsync(Guid operationId, Task<RemoteOutboundProxyProbeResult> pending)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeCancellationAcknowledgementTimeoutSeconds));
        try
        {
            var acknowledged = await InvokeAsync(() => _rpc.CancelProbeAsync(new RemoteOutboundProxyProbeCancelRequest(operationId), cleanup.Token))
                .WaitAsync(cleanup.Token).ConfigureAwait(false);
            if (!acknowledged)
                throw new IOException("The target Host refused outbound probe cleanup.");
            await ObserveProbeCompletionAsync(pending, cleanup.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _ = pending.ContinueWith(static completed => { _ = completed.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw new IOException("The target Host did not confirm outbound probe cleanup.", error);
        }
    }

    private static async Task ObserveProbeCompletionAsync(Task<RemoteOutboundProxyProbeResult> pending, CancellationToken cancellationToken)
    {
        try { await pending.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (pending.IsCanceled) { }
        catch (Exception error) when (pending.IsFaulted && error is JsonRpcException or IOException) { }
    }

    private static async Task<T> InvokeAsync<T>(Func<Task<T>> invoke)
    {
        try { return await invoke().ConfigureAwait(false); }
        catch (RemoteRpcException error) when (error.ErrorCode is { } code && code != StreamJsonRpc.Protocol.JsonRpcErrorCode.MethodNotFound)
        {
            throw new JsonRpcException((int)code, error.Message, error.ErrorData is JsonElement data ? data.Clone() : null);
        }
        catch (ConnectionLostException error)
        {
            throw new IOException("The target Host disconnected during the outbound proxy request.", error);
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_host.Supports(NetworkProtocol.ReloadCapability))
            throw new NotSupportedException("The target Host does not support network.reload; upgrade it to reload its own network settings.");
        await InvokeAsync(() => _rpc.ReloadAsync(cancellationToken)).ConfigureAwait(false);
    }
}
