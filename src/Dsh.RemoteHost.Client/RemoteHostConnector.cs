using System.Net.Sockets;
using System.Text;
using Dsh.Transport;

namespace Dsh.RemoteHost;

public static class RemoteHostConnector
{
    private const int ProbeConnectAttempts = 10;
    private static readonly TimeSpan HostProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromMilliseconds(700);

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
}

