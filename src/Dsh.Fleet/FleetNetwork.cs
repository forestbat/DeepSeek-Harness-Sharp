using Dsh.RemoteHost;
using Dsh.Runtime;

namespace Dsh.Fleet;

/** Outbound settings belong to the target Host; Fleet only borrows its existing connection. */
public sealed class FleetNetwork(MachineConnection connection, Action<string>? probeReady = null)
{
    public async Task<RemoteOutboundProxyState> GetOutboundAsync(CancellationToken cancellationToken = default)
    {
        var (lease, _) = await BorrowAsync(cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            return await lease.Client.Network.GetOutboundAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lease.Lifetime.IsCancellationRequested)
        {
            throw new IOException("The target Host disconnected during the outbound proxy request.");
        }
    }

    public async Task<RemoteOutboundProxyState> SetOutboundAsync(RemoteOutboundProxySetRequest request, CancellationToken cancellationToken = default)
    {
        var (lease, _) = await BorrowAsync(cancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            return await lease.Client.Network.SetOutboundAsync(request, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lease.Lifetime.IsCancellationRequested)
        {
            throw new IOException("The target Host disconnected during the outbound proxy request.");
        }
    }

    public async Task<RemoteOutboundProxyProbeResult> ProbeOutboundAsync(RemoteOutboundProxyProbeRequest request, CancellationToken cancellationToken = default)
    {
        var capability = request.OperationId is null ? NetworkProtocol.Capability : NetworkProtocol.CancellableProbeCapability;
        var (lease, home) = await BorrowAsync(cancellationToken, capability).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        lifetime.Token.ThrowIfCancellationRequested();
        probeReady?.Invoke(home);
        try
        {
            return await lease.Client.Network.ProbeOutboundAsync(request, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lease.Lifetime.IsCancellationRequested)
        {
            throw new IOException("The target Host disconnected during the outbound proxy request.");
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var (lease, _) = await BorrowAsync(cancellationToken, NetworkProtocol.ReloadCapability).ConfigureAwait(false);
        await ReloadLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
    }

    /** Reload only a currently connected local owner; a reconnect is not authorization to change another home. */
    public Task ReloadLocalAsync(string expectedHome, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!connection.Machine.IsLocal)
            throw new InvalidOperationException("Only a local Host can receive a same-home outbound reload.");
        if (connection.CurrentHostLease is not { } current)
            return Task.CompletedTask;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedHome)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(current.Info.HomeRoot)), comparison))
            throw new InvalidOperationException("The current local Host uses a different harness home; reconnect before reloading.");
        if (!current.Lease.Client.Supports(NetworkProtocol.ReloadCapability))
            throw new NotSupportedException("The local Host does not support outbound reload; upgrade and reconnect it.");
        return ReloadLeaseAsync(current.Lease, cancellationToken);
    }

    private static async Task ReloadLeaseAsync(HostLease lease, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        try
        {
            await lease.Client.Network.ReloadAsync(lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && lease.Lifetime.IsCancellationRequested)
        {
            throw new IOException("The target Host disconnected during the network reload request.");
        }
    }

    private async Task<(HostLease Lease, string Home)> BorrowAsync(CancellationToken cancellationToken, string capability = NetworkProtocol.Capability)
    {
        await connection.WaitSettledAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var current = connection.CurrentHostLease
            ?? throw new IOException(OutboundProxy.Redact(
                $"The target Host is not connected: {connection.Detail ?? "connection unavailable"}",
                connection.Machine.Password, connection.Machine.Proxy));
        var lease = current.Lease;
        if (lease.Lifetime.IsCancellationRequested)
            throw new IOException("The target Host disconnected before the outbound proxy request.");
        if (!lease.Client.Supports(capability))
            throw new NotSupportedException($"The target Host does not support {capability}; upgrade the target Host to use this network operation.");
        return (lease, current.Info.HomeRoot);
    }
}

