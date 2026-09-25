using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dsh.Account;

/**
 * 本机回环回调服务器: TcpListener 手写单请求 HTTP(Windows 上 HttpListener 有 URL ACL 坑)。
 * 只接受 GET /oauth/callback, state 恒时比较, code/state 各只允许一个; 用后即关。
 */
public sealed class LoopbackCallbackServer : IAsyncDisposable
{
    private const int MaxHeaderLines = 64;

    private const int RequestReadTimeoutSeconds = 30;

    private static readonly byte[] SuccessPage = Encoding.UTF8.GetBytes(
        "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><title>登录成功</title><body><p>登录成功，可以关闭此页。</p></body></html>");

    private readonly TcpListener _listener;

    public LoopbackCallbackServer() => _listener = new TcpListener(IPAddress.Loopback, 0);

    public int Port { get; private set; }

    public string RedirectUri => $"http://127.0.0.1:{Port}{AccountOrigins.CallbackPath}";

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public async Task<string?> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return null;
            }
            using (client)
            {
                var code = await HandleClientAsync(client, expectedState, cancellationToken);
                if (code is not null)
                    return code;
            }
        }
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        return ValueTask.CompletedTask;
    }

    private static async Task<string?> HandleClientAsync(TcpClient client, string expectedState, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(RequestReadTimeoutSeconds));
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        try
        {
            var requestLine = await reader.ReadLineAsync(timeout.Token);
            if (requestLine is null)
                return null;
            for (var line = 0; line < MaxHeaderLines; line++)
            {
                var header = await reader.ReadLineAsync(timeout.Token);
                if (string.IsNullOrEmpty(header))
                    break;
            }
            return await ProcessAsync(stream, requestLine, expectedState, timeout.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static async Task<string?> ProcessAsync(NetworkStream stream, string requestLine, string expectedState, CancellationToken cancellationToken)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length != 3 || !string.Equals(parts[0], "GET", StringComparison.Ordinal))
        {
            await RespondAsync(stream, 405, "Method Not Allowed", cancellationToken);
            return null;
        }
        var requestUri = TryParseTarget(parts[1]);
        var code = requestUri is null ? null : SingleQueryValue(requestUri, "code");
        var state = requestUri is null ? null : SingleQueryValue(requestUri, "state");
        if (requestUri is null || requestUri.AbsolutePath != AccountOrigins.CallbackPath || code is null || state is null
            || !AccountPkce.FixedTimeEquals(expectedState, state))
        {
            await RespondAsync(stream, 400, "Bad Request", cancellationToken);
            return null;
        }
        await RespondAsync(stream, 200, "OK", cancellationToken, SuccessPage);
        return code;
    }

    private static Uri? TryParseTarget(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute))
            return absolute;
        return Uri.TryCreate($"http://127.0.0.1{target}", UriKind.Absolute, out var relative) ? relative : null;
    }

    private static string? SingleQueryValue(Uri uri, string name)
    {
        var values = new List<string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0 || !string.Equals(Uri.UnescapeDataString(pair[..separator]), name, StringComparison.Ordinal))
                continue;
            values.Add(Uri.UnescapeDataString(pair[(separator + 1)..]));
        }
        return values.Count == 1 && values[0].Length > 0 ? values[0] : null;
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string reason, CancellationToken cancellationToken, byte[]? body = null)
    {
        body ??= [];
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken);
        if (body.Length > 0)
            await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
