namespace Dsh.RemoteHost;

/** The server plugin's composition service; the host owns options and backend lifetimes. */
public sealed class RemoteHostServerFactory
{
    public const string ServiceName = "remoteHostServerFactory";

    public RemoteHostServer Create(RemoteHostServerOptions options, IRemoteHostBackend backend)
        => new(options, backend);
}
