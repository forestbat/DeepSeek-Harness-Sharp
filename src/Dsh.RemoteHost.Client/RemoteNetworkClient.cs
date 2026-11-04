using static Dsh.RemoteHost.RemoteHostRpc;

namespace Dsh.RemoteHost;

/** Client implementation of the optional Network domain. */
public sealed class RemoteNetworkClient : IWorkspaceNetwork
{
    private const int ProbeCancellationAcknowledgementTimeoutSeconds = 5;
    private readonly RemoteHostClient _host;

    internal RemoteNetworkClient(RemoteHostClient host) => _host = host;

    public Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default)
        => _host.SendAsync(NetworkProtocol.MethodOutboundGet, null, NetworkProtocolJsonContext.Default.RemoteOutboundProxyState, cancellationToken);

    public Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default)
        => _host.SendAsync(NetworkProtocol.MethodOutboundSet, Serialize(request, NetworkProtocolJsonContext.Default.RemoteOutboundProxySetRequest), NetworkProtocolJsonContext.Default.RemoteOutboundProxyState, cancellationToken);

    public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default)
    {
        if (!_host.Supports(NetworkProtocol.CancellableProbeCapability))
            return await _host.SendAsync(NetworkProtocol.MethodOutboundProbe, Serialize(request, NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeRequest), NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeResult, cancellationToken).ConfigureAwait(false);

        if (request.OperationId is null)
            request = request with { OperationId = Guid.NewGuid() };
        try
        {
            return await _host.SendAsync(NetworkProtocol.MethodOutboundProbe, Serialize(request, NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeRequest), NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(ProbeCancellationAcknowledgementTimeoutSeconds));
            bool acknowledged;
            try
            {
                acknowledged = await _host.SendAsync(NetworkProtocol.MethodOutboundProbeCancel,
                    Serialize(new RemoteOutboundProxyProbeCancelRequest(request.OperationId.Value), NetworkProtocolJsonContext.Default.RemoteOutboundProxyProbeCancelRequest),
                    HostProtocolJsonContext.Default.Boolean, cleanup.Token).ConfigureAwait(false);
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
        await _host.SendAsync(NetworkProtocol.MethodReload, null, HostProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
    }
}


