using System.Diagnostics;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Fleet;

/**
 * One established transport to a machine's host: a local loopback connection, or an ssh process carrying the stdio bridge.
 * Exited completes when the ssh process ends; local links have no process and rely on the heartbeat.
 */
internal sealed class HostLink(RemoteHostClient client, SshHostLauncher.Connection? ssh) : IAsyncDisposable
{
    /** How many 100 ms connection attempts a freshly started local host gets to compose its plugins and bind its socket. */
    private const int StartedHostConnectAttempts = 300;

    public RemoteHostClient Client { get; } = client;

    public Process? Process => ssh?.Process;

    public Task Exited => ssh?.Process.WaitForExitAsync() ?? Client.WhenClosed;

    /** Connects to the local host of the machine's harness home, starting it first through startHost when it is not reachable. */
    public static async Task<HostLink> OpenLocalAsync(MachineSpec machine, FleetOptions options, CancellationToken cancellationToken)
    {
        var root = LocalRoot(machine);
        var attempts = 1;
        if (options.StartLocalHost is { } startHost)
        {
            if (await RemoteHostConnector.TryConnectClientAsync(root, null, options.ClientName, cancellationToken).ConfigureAwait(false) is { } existing)
                return new HostLink(existing, null);
            cancellationToken.ThrowIfCancellationRequested();
            await startHost(machine, cancellationToken).ConfigureAwait(false);
            attempts = StartedHostConnectAttempts;
        }

        var stream = await RemoteHostConnector.ConnectAsync(root, cancellationToken, attempts).ConfigureAwait(false);
        var connected = await RemoteHostClient.ConnectAsync(stream, null, options.ClientName, cancellationToken).ConfigureAwait(false);
        return new HostLink(connected, null);
    }

    public static async Task<HostLink> OpenSshAsync(MachineSpec machine, string? commandPath, FleetOptions options, CancellationToken cancellationToken)
    {
        var connection = await SshHostLauncher.ConnectAsync(machine, commandPath, options.ClientName, cancellationToken, options.SshTransportAdapter).ConfigureAwait(false);
        return new HostLink(connection.Client, connection);
    }

    /** The endpoint root of a local machine: its configured harness home, or the default one (DSH_HOME or ~/.dsh, following .storage-root). */
    public static string LocalRoot(MachineSpec machine)
        => string.IsNullOrWhiteSpace(machine.HarnessHome) ? LocalEndpointRoot.Resolve() : LocalEndpointRoot.FollowMarker(machine.HarnessHome);

    public async ValueTask DisposeAsync()
    {
        if (ssh is not null)
            await ssh.DisposeAsync().ConfigureAwait(false);
        else
            await Client.DisposeAsync().ConfigureAwait(false);
    }
}
