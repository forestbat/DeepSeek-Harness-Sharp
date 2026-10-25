namespace Dsh.RemoteHost;

/** The client plugin's connection service; consumers that need remote capability resolve this instead of constructing a connector. */
public sealed class RemoteHostClientService
{
    public const string ServiceName = "remoteHostClient";

    public Task<RemoteHostClient?> TryConnectAsync(
        string? root,
        string? token,
        string? clientName,
        CancellationToken cancellationToken = default)
        => RemoteHostConnector.TryConnectClientAsync(root, token, clientName, cancellationToken);
}
