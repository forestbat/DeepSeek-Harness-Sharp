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
        => _rpc.GetOutboundAsync(cancellationToken);

    public async Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _rpc.SetOutboundAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteRpcException error) when (error.ErrorCode is { } code && (int)code is NetworkProtocol.RevisionConflict or JsonRpcException.InvalidParams)
        {
            throw new JsonRpcException((int)code, error.Message, error.ErrorData is JsonElement data ? data.Clone() : null);
        }
    }

    public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_host.Supports(NetworkProtocol.CancellableProbeCapability))
            return await _rpc.ProbeAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.OperationId is null)
            request = request with { OperationId = Guid.NewGuid() };
        var pending = _rpc.ProbeAsync(request, cancellationToken);
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
            var acknowledged = await _rpc.CancelProbeAsync(new RemoteOutboundProxyProbeCancelRequest(operationId), cleanup.Token)
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
        catch (Exception error) when (pending.IsFaulted && error is RemoteRpcException or ConnectionLostException) { }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_host.Supports(NetworkProtocol.ReloadCapability))
            throw new NotSupportedException("The target Host does not support network.reload; upgrade it to reload its own network settings.");
        await _rpc.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }
}
