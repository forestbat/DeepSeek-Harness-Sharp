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
    private const int ProbeConnectAttempts = 10;
    private static readonly TimeSpan HostProbeTimeout = TimeSpan.FromSeconds(5);

    /** 单次 TCP connect 的上限: 端口文件可能指向已退出的 host, Windows 的 SYN 重传要 ~2s, 不能让它拖住每次重试。 */
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromMilliseconds(700);

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

    /**
     * 面向局域网的 TCP 监听: 连接直接进入 JSON-RPC, 访问控制由握手里的 token(RemoteHostServerOptions.Token)负责。
     * 与 loopback 监听不同, 这里没有 run 目录里的 LocalEndpointToken 前导; 调用方必须给出 token, 否则一条裸端口就能拿到宿主的全部能力。
     * listening 在真正绑定后回调, 端口写 0 时调用方据此拿到系统分配的端口。
     */
    public static async Task ServeTcpAsync(RemoteHostServer server, IPEndPoint endpoint, CancellationToken cancellationToken, Func<IPEndPoint, Task>? listening = null)
    {
        if (!endpoint.Address.Equals(IPAddress.Loopback) && !server.HasToken)
            throw new InvalidOperationException("a host listening on a non-loopback address requires an access token");
        var listener = new TcpListener(endpoint);
        listener.Start();
        try
        {
            if (listening is not null)
                await listening((IPEndPoint)listener.LocalEndpoint).ConfigureAwait(false);
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
                    break;
                }

                _ = ServeConnectionAsync(server, new NetworkStream(client.Client, ownsSocket: true), client, cancellationToken);
            }
        }
        finally
        {
            listener.Stop();
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

    /** 局域网连接: token 在 JSON-RPC 握手里校验, 这里只负责把连接交给宿主。 */
    private static async Task ServeConnectionAsync(RemoteHostServer server, Stream stream, IDisposable owner, CancellationToken cancellationToken)
    {
        using (owner)
        await using (stream)
        {
            try
            {
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

    /** Probes the local transport for read-only diagnostics. */
    public static async Task<bool> IsReachableAsync(string? root, CancellationToken cancellationToken)
    {
        try
        {
            using var stream = await ConnectAsync(root, cancellationToken, attempts: 10).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is SocketException or IOException or TimeoutException)
        {
            return false;
        }
    }

    /** Probes an existing Host handshake within a fixed budget and returns its usable client. */
    public static async Task<RemoteHostClient?> TryConnectClientAsync(
        string? root, string? token, string? clientName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(HostProbeTimeout);
        Stream stream;
        try
        {
            stream = await ConnectAsync(root, probe.Token, ProbeConnectAttempts).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or IOException or TimeoutException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        try
        {
            return await RemoteHostClient.ConnectAsync(stream, token, clientName, probe.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /** An owned startup remains pending until its process exits or the endpoint becomes reachable; ordinary connects retain their probe budget. */
    public static async Task<Stream> ConnectAsync(string? root, CancellationToken cancellationToken, int attempts = 50, Task? startupLifetime = null)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < attempts || startupLifetime is { IsCompleted: false }; attempt++)
        {
            try
            {
                var stream = await ConnectEndpointAsync(root, cancellationToken).ConfigureAwait(false);
                return await PresentTokenAsync(stream, root, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is SocketException or IOException or FormatException)
            {
                last = error;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("host daemon not reachable", last);
    }

    private static async Task<Stream> ConnectEndpointAsync(string? root, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return await TransportConnection.ConnectUnixAsync(RemoteHostEndpoint.SocketPath(root), cancellationToken).ConfigureAwait(false);
        var portFile = RemoteHostEndpoint.PortFile(root);
        if (!File.Exists(portFile))
            throw new IOException("host daemon not started");
        int port;
        await using (var file = new FileStream(portFile, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, useAsync: true))
        using (var reader = new StreamReader(file))
            port = int.Parse((await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim());
        // 直接连: GetActiveTcpListeners 在部分 Windows 环境不列出刚建立/跨进程的 loopback listener(实测 host 可连但表里没有),
        // 用它做前置判断会把正常的 host 误判成 "not listening", GUI 因此一直卡在等 host、窗口不出现。
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(ConnectAttemptTimeout);
        try
        {
            return await TransportConnection.ConnectTcpAsync("127.0.0.1", port, attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("host daemon not listening", new TimeoutException("loopback connect timed out"));
        }
    }

    /**
     * Sends the token the daemon issued before it started listening; it is read only after the connect succeeded, because a daemon that is not running has no token yet.
     * A reachable daemon without a token is not retried as unreachable: that would spawn a second daemon that deletes the live socket.
     */
    private static async Task<Stream> PresentTokenAsync(Stream stream, string? root, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = RemoteHostEndpoint.EndpointFile(root);
            var token = LocalEndpointToken.Read(endpoint)
                ?? throw new InvalidOperationException(
                    $"the dsharp host daemon at {endpoint} issued no access token ({LocalEndpointToken.PathFor(endpoint)}); it was started by an older build, stop that daemon process and retry");
            await stream.WriteAsync(Encoding.UTF8.GetBytes(token), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
