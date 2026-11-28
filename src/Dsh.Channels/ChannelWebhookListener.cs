using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Dsh.Channels;

public sealed class ChannelWebhookListener(
    WebhookChannelAdapter github,
    SlackChannelAdapter slack,
    DiscordChannelAdapter discord) : IAsyncDisposable, IDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 2 * 1024 * 1024;

    private readonly Lock _gate = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptLoop;

    public int Port { get; private set; }
    public Uri? BaseUri { get; private set; }
    public bool IsListening
    {
        get
        {
            lock (_gate)
                return _listener is not null;
        }
    }

    public Task<int> StartListenerAsync(int port = 0, string host = "127.0.0.1", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_listener is not null)
                return Task.FromResult(Port);

            var address = string.IsNullOrWhiteSpace(host) || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                ? IPAddress.Loopback
                : IPAddress.Parse(host);
            var listener = new TcpListener(address, port);
            listener.Start();
            var boundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var cts = new CancellationTokenSource();
            _listener = listener;
            _lifetime = cts;
            Port = boundPort;
            BaseUri = new Uri($"http://{address}:{boundPort}");
            _acceptLoop = AcceptLoopAsync(listener, cts.Token);
            return Task.FromResult(boundPort);
        }
    }

    public async Task StopAsync()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            listener = _listener;
            cts = _lifetime;
            loop = _acceptLoop;
            _listener = null;
            _lifetime = null;
            _acceptLoop = null;
            Port = 0;
            BaseUri = null;
        }

        if (cts is not null)
            await cts.CancelAsync().ConfigureAwait(false);
        listener?.Stop();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
        cts?.Dispose();
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }
            _ = HandleConnectionAsync(client, cancellationToken);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var request = await ReadHttpRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request is null)
                {
                    await WriteResponseAsync(stream, 400, JsonError("Malformed HTTP request."), timeout.Token).ConfigureAwait(false);
                    return;
                }

                var result = await RouteRequestAsync(request.Method, request.Path, request.Headers, request.Body, timeout.Token)
                    .ConfigureAwait(false);
                await WriteResponseAsync(stream, result.StatusCode, result.JsonBody, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            {
                // Connection closed or cancelled during shutdown.
            }
            catch (Exception error)
            {
                await WriteResponseAsync(stream, 500, JsonError(error.Message), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<WebhookHandleResult> RouteRequestAsync(
        string method,
        string rawPath,
        IReadOnlyDictionary<string, string> headers,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var queryIndex = rawPath.IndexOf('?');
        var path = queryIndex >= 0 ? rawPath[..queryIndex] : rawPath;

        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            && string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
        {
            return new WebhookHandleResult(200, JsonSerializer.Serialize(new WebhookAckResponse(true, "healthy"), DshChannelsJsonContext.Default.WebhookAckResponse));
        }

        if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            return new WebhookHandleResult(405, JsonError("Method not allowed."));

        return path.ToLowerInvariant() switch
        {
            "/webhooks/github" => await github.HandleInboundAsync(headers, body, cancellationToken).ConfigureAwait(false),
            "/webhooks/slack" => await slack.HandleInboundAsync(headers, body, cancellationToken).ConfigureAwait(false),
            "/webhooks/discord" => await discord.HandleInboundAsync(headers, body, cancellationToken).ConfigureAwait(false),
            _ => new WebhookHandleResult(404, JsonError($"Route '{path}' not found.")),
        };
    }

    private static async Task<ParsedHttpRequest?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headerBuffer = new List<byte>(1024);
        var chunk = new byte[1024];
        var headerEnd = -1;

        while (headerBuffer.Count < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return null;
            headerBuffer.AddRange(chunk.AsSpan(0, read));
            headerEnd = FindHeaderTerminator(headerBuffer);
            if (headerEnd >= 0)
                break;
        }

        if (headerEnd < 0)
            return null;

        var headerBytes = headerBuffer.Take(headerEnd).ToArray();
        var headerText = Encoding.ASCII.GetString(headerBytes);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0)
            return null;

        var requestParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
                continue;
            var key = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            headers[key] = value;
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthHeader))
        {
            if (!int.TryParse(lengthHeader, out contentLength) || contentLength < 0 || contentLength > MaxBodyBytes)
                return null;
        }

        var bodyStart = headerEnd + 4;
        var leftoverCount = headerBuffer.Count - bodyStart;
        var body = new byte[contentLength];
        if (contentLength > 0)
        {
            var copyCount = Math.Min(leftoverCount, contentLength);
            if (copyCount > 0)
                headerBuffer.CopyTo(bodyStart, body, 0, copyCount);
            if (copyCount < contentLength)
                await stream.ReadExactlyAsync(body.AsMemory(copyCount, contentLength - copyCount), cancellationToken).ConfigureAwait(false);
        }

        return new ParsedHttpRequest(requestParts[0], requestParts[1], headers, body);
    }

    private static int FindHeaderTerminator(List<byte> buffer)
    {
        for (var i = 0; i <= buffer.Count - 4; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, int statusCode, string jsonBody, CancellationToken cancellationToken)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(jsonBody);
        var reason = statusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            405 => "Method Not Allowed",
            _ => "Internal Server Error",
        };
        var headers = $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bodyBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string JsonError(string message)
        => JsonSerializer.Serialize(new WebhookAckResponse(false, Error: message), DshChannelsJsonContext.Default.WebhookAckResponse);

    private sealed record ParsedHttpRequest(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);
}
