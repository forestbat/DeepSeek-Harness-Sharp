using Dsh.RemoteHost;

namespace Dsh.Host;

/** Bridges one stdio connection to the resident host daemon; ending the bridge leaves host sessions alive. */
public static class HostBridge
{
    public static async Task RunAsync(Stream duplex, string? root, CancellationToken cancellationToken)
    {
        using var daemon = await RemoteHostConnector.ConnectAsync(root, cancellationToken).ConfigureAwait(false);
        var toDaemon = PumpAsync(duplex, daemon, cancellationToken);
        var toClient = PumpAsync(daemon, duplex, cancellationToken);
        await Task.WhenAny(toDaemon, toClient).ConfigureAwait(false);
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken cancellationToken)
    {
        var buffer = new byte[16384];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await to.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
        }
    }
}
