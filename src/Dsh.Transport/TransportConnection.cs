using System.IO.Pipes;
using System.Net.Sockets;

namespace Dsh.Transport;

/**
 * 建立到远端 host 的双向字节流。通道与上层 JSON-RPC 解耦:
 * - stdio-over-SSH: FromStream(进程 stdin/stdout 或测试用的双工流);
 * - 本地/隧道: Unix domain socket、TCP loopback、Windows 命名管道。
 */
public static class TransportConnection
{
    /** 直接用一条已有的双向流(stdio-over-SSH、内存/回环测试)。调用方负责其生存期。 */
    public static Stream FromStream(Stream duplex)
    {
        ArgumentNullException.ThrowIfNull(duplex);
        return duplex;
    }

    public static async Task<Stream> ConnectUnixAsync(string socketPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true);
    }

    public static async Task<Stream> ConnectTcpAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(host);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new NetworkStream(client.Client, ownsSocket: true);
    }

    public static async Task<Stream> ConnectNamedPipeAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return pipe;
    }
}
