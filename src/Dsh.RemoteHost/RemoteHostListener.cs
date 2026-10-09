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

    /** 常驻 daemon 是否可达(单次尝试, 不等待)。 */
    public static async Task<bool> IsReachableAsync(string? root, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await ConnectAsync(root, cancellationToken, attempts: 1).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is SocketException or IOException or TimeoutException)
        {
            return false;
        }
    }

    /** 连接到常驻 daemon; 带重试, 覆盖“刚 spawn 起来还没 bind”的窗口。 */
    public static async Task<Stream> ConnectAsync(string? root, CancellationToken cancellationToken, int attempts = 50)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    var portFile = RemoteHostEndpoint.PortFile(root);
                    if (!File.Exists(portFile))
                        throw new IOException("host daemon not started");
                    var port = int.Parse((await File.ReadAllTextAsync(portFile, cancellationToken).ConfigureAwait(false)).Trim());
                    return await TransportConnection.ConnectTcpAsync("127.0.0.1", port, cancellationToken).ConfigureAwait(false);
                }

                return await TransportConnection.ConnectUnixAsync(RemoteHostEndpoint.SocketPath(root), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or IOException or FormatException)
            {
                last = error;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("host daemon not reachable", last);
    }

    /** 把一条 stdio 双向流桥接到常驻 daemon: ssh 会话断开只断这条桥, daemon 与其上的会话继续跑(脱钩/重连)。 */
    public static async Task BridgeAsync(Stream duplex, string? root, CancellationToken cancellationToken)
    {
        using var daemon = await ConnectAsync(root, cancellationToken).ConfigureAwait(false);
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
            // 对端关闭/取消/流被释放: 泵结束即可。
        }
    }
}
