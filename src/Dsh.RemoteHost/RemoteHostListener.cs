using System.Net;
using System.Net.Sockets;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/** loopback 监听: Unix domain socket(非 Windows) 或 TCP loopback + 端口文件(Windows); 逐连接服务。 */
public static class RemoteHostListener
{
    public static async Task ServeLoopbackAsync(
        RemoteHostServer server,
        string? root,
        CancellationToken cancellationToken)
    {
        var run = RemoteHostEndpoint.RunDirectory(root);
        Directory.CreateDirectory(run);
        if (OperatingSystem.IsWindows())
            await ServeTcpAsync(server, root, cancellationToken).ConfigureAwait(false);
        else
            await ServeUnixAsync(server, root, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ServeTcpAsync(RemoteHostServer server, string? root, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var portFile = RemoteHostEndpoint.PortFile(root);
        await File.WriteAllTextAsync(portFile, ((IPEndPoint)listener.LocalEndpoint).Port.ToString(), cancellationToken).ConfigureAwait(false);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                await server.ServeAsync(new NetworkStream(client.Client, ownsSocket: false), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            listener.Stop();
            TryDelete(portFile);
        }
    }

    private static async Task ServeUnixAsync(RemoteHostServer server, string? root, CancellationToken cancellationToken)
    {
        var socketPath = RemoteHostEndpoint.SocketPath(root);
        TryDelete(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(8);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await server.ServeAsync(new NetworkStream(socket, ownsSocket: true), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            TryDelete(socketPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
