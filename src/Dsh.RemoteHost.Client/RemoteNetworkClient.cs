using Dsh.RemoteHost.Rpc;

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

    public Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default)
        => _rpc.SetOutboundAsync(request, cancellationToken);

    public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default)
    {
        if (!_host.Supports(NetworkProtocol.CancellableProbeCapability))
            return await _rpc.ProbeAsync(request, cancellationToken).ConfigureAwait(false);

        if (request.OperationId is null)
            request = request with { OperationId = Guid.NewGuid() };
        try
        {
            return await _rpc.ProbeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeCancellationAcknowledgementTimeoutSeconds));
            bool acknowledged;
            try
            {
                acknowledged = await _rpc.CancelProbeAsync(new RemoteOutboundProxyProbeCancelRequest(request.OperationId.Value), cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                throw new IOException("The target Host did not confirm outbound probe cleanup.", error);
            }
            if (!acknowledged)
                throw new IOException("The target Host did not confirm outbound probe cleanup.");
            throw;
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (!_host.Supports(NetworkProtocol.ReloadCapability))
            throw new NotSupportedException("The target Host does not support network.reload; upgrade it to reload its own network settings.");
        await _rpc.ReloadAsync(cancellationToken).ConfigureAwait(false);
    }
}
