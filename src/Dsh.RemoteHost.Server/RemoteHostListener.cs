using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Dsh.Transport;

namespace Dsh.RemoteHost;

/**
 * loopback 监听: Unix domain socket(非 Windows) 或 TCP loopback + 端口文件(Windows); 逐连接服务。
 * Every connection must open with the daemon's LocalEndpointToken before any JSON-RPC frame; the stdio bridge presents it on behalf of the SSH client.
 * TCP port files are immutable snapshots: publish a complete sibling by rename; readers allow replacement while holding the old snapshot.
 */
public static class RemoteHostListener
{


    public static async Task ServeLoopbackAsync(
        RemoteHostServer server,
        string? root,
        CancellationToken cancellationToken)
    {
        // The token is issued into the owner-only run directory before the endpoint becomes reachable.
        using var token = LocalEndpointToken.Issue(RemoteHostEndpoint.EndpointFile(root));
        if (OperatingSystem.IsWindows())
            await ServeTcpAsync(server, root, token, cancellationToken).ConfigureAwait(false);
        else
            await ServeUnixAsync(server, root, token, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ServeTcpAsync(RemoteHostServer server, string? root, LocalEndpointToken token, CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var portFile = RemoteHostEndpoint.PortFile(root);
        var temporary = $"{portFile}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, ((IPEndPoint)listener.LocalEndpoint).Port.ToString(), cancellationToken).ConfigureAwait(false);
            if (File.Exists(portFile))
                File.Replace(temporary, portFile, destinationBackupFileName: null);
            else
                File.Move(temporary, portFile);
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (cancellationToken.IsCancellationRequested
                    && error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // 取消时底层可能以 SocketException(而非 OperationCanceledException)结束挂起的 accept。
                    break;
                }

                // 每条连接独立服务: 一个半开/卡住的连接不能阻塞后续连接。
                _ = ServeConnectionAsync(server, new NetworkStream(client.Client, ownsSocket: true), client, token, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
            TryDelete(temporary);
            TryDelete(portFile);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task ServeUnixAsync(RemoteHostServer server, string? root, LocalEndpointToken token, CancellationToken cancellationToken)
    {
        var socketPath = RemoteHostEndpoint.SocketPath(root);
        using var listener = OwnerOnlyFiles.ListenUnixSocket(socketPath, 8);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (cancellationToken.IsCancellationRequested
                    && error is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // 取消时底层可能以 SocketException(而非 OperationCanceledException)结束挂起的 accept。
                    break;
                }

                // 每条连接独立服务: 一个半开/卡住的连接不能阻塞后续连接。
                _ = ServeConnectionAsync(server, new NetworkStream(socket, ownsSocket: true), socket, token, cancellationToken);
            }
        }
        finally
        {
            TryDelete(socketPath);
        }
    }

    /**
     * 服务一条连接; 任何连接级异常只终结该连接, 不影响监听循环。
     * A connection without the matching token is closed before the host serves it.
     */
    private static async Task ServeConnectionAsync(RemoteHostServer server, Stream stream, IDisposable owner, LocalEndpointToken token, CancellationToken cancellationToken)
    {
        using (owner)
        await using (stream)
        {
            try
            {
                if (await PresentsTokenAsync(stream, token, cancellationToken).ConfigureAwait(false))
                    await server.ServeAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
            {
            }
        }
    }

    /** Reads exactly the token's length, so no byte of the JSON-RPC stream behind it is consumed; an early EOF surfaces as EndOfStreamException. */
    private static async Task<bool> PresentsTokenAsync(Stream stream, LocalEndpointToken token, CancellationToken cancellationToken)
    {
        var preamble = new byte[LocalEndpointToken.ValueLength];
        await stream.ReadExactlyAsync(preamble, cancellationToken).ConfigureAwait(false);
        return token.Matches(Encoding.UTF8.GetString(preamble));
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
