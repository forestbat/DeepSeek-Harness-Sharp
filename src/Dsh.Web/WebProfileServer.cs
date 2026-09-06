using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dsh.Web;

public sealed class WebProfileServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _acceptLoop;
    private bool _stopped;

    public WebProfileServer(int? port = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, port ?? 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public async Task StopAsync()
    {
        if (_stopped)
            return;
        _stopped = true;
        _cancellation.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (OperationCanceledException)
        {
        }
        _cancellation.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task AcceptLoopAsync()
    {
        while (!_cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }
            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var request = await ReadRequestAsync(stream, _cancellation.Token);
            if (request is null)
                return;
            var (status, contentType, body) = Route(request);
            var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(header + body), _cancellation.Token);
        }
    }

    private static (string Status, string ContentType, string Body) Route(string request)
    {
        var firstLine = request.Split('\n', 2)[0].Trim();
        var parts = firstLine.Split(' ', 3);
        var method = parts.Length > 0 ? parts[0] : "";
        var path = parts.Length > 1 ? parts[1].Split('?')[0] : "/";
        if (method == "GET" && path == "/")
            return ("200 OK", "text/html; charset=utf-8", "<!doctype html><html><head><title>DeepSeek Harness</title></head><body><h1>DeepSeek Harness</h1><p>The C# web profile is running.</p></body></html>");
        if (method == "GET" && path == "/api/health")
            return ("200 OK", "application/json", """{"ok":true,"name":"deepseek-harness-web"}""");
        return ("404 Not Found", "text/plain; charset=utf-8", "not found");
    }

    private static async Task<string?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();
        while (builder.Length < 8192)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                return null;
            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }
        return builder.ToString();
    }
}
