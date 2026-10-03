using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dsh.Runtime;

namespace Dsh.A2A;

/** HTTP + SSE 传输层：GET agent-card、POST JSON-RPC、message/stream 与 tasks/resubscribe 走 SSE。 */
public sealed class A2aHttpListener : IDisposable
{
    public const string AgentCardPath = "/.well-known/agent-card.json";

    private const string VersionHeader = "A2A-Version";
    private const string JsonContentType = "application/json";
    private const string SseContentType = "text/event-stream";

    private readonly A2aJsonRpc _rpc;
    private readonly A2aServerOptions _options;
    private readonly Logger _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private string _cardJson = "";
    private Task? _acceptLoop;

    public A2aHttpListener(A2aJsonRpc rpc, A2aServerOptions options, Context ctx)
    {
        _rpc = rpc;
        _options = options;
        _logger = ctx.LoggerFor("a2a");
    }

    public string Endpoint { get; private set; } = "";

    public void Start()
    {
        var host = string.IsNullOrEmpty(_options.Host) ? "127.0.0.1" : _options.Host;
        var port = _options.Port > 0 ? _options.Port : ProbeFreePort();
        _listener.Prefixes.Add($"http://{host}:{port}/");
        _listener.Start();
        Endpoint = $"http://{host}:{port}";
        _cardJson = A2aWire.CardJson(A2aAgentCardBuilder.Build(Endpoint, _options));
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop?.Wait();
        }
        catch (AggregateException)
        {
        }
        _listener.Close();
        _stop.Dispose();
    }

    private static int ProbeFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            context.Response.Headers[VersionHeader] = A2aAgentCardBuilder.ProtocolVersion;
            if (context.Request.HttpMethod == "GET"
                && string.Equals(context.Request.Url?.AbsolutePath, AgentCardPath, StringComparison.Ordinal))
            {
                await WriteJsonAsync(context.Response, _cardJson, 200);
                return;
            }
            if (!Authorize(context.Request))
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }
            if (context.Request.HttpMethod != "POST")
            {
                context.Response.StatusCode = 405;
                context.Response.Close();
                return;
            }
            if (context.Request.Headers[VersionHeader] is { } version && version != A2aAgentCardBuilder.ProtocolVersion)
            {
                await WriteJsonAsync(context.Response,
                    ErrorEnvelope(null, A2aErrorCodes.InvalidRequest, $"unsupported A2A-Version: {version}"), 400);
                return;
            }
            await DispatchRequestAsync(context);
        }
        catch (Exception error) when (error is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            _logger.Warn($"a2a request failed: {error.Message}");
            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private bool Authorize(HttpListenerRequest request)
    {
        if (_options.AuthToken is not { Length: > 0 } token)
            return true;
        return string.Equals(request.Headers["Authorization"], $"Bearer {token}", StringComparison.Ordinal);
    }

    private async Task DispatchRequestAsync(HttpListenerContext context)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(await ReadBodyAsync(context.Request));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("jsonrpc", out var versionElement) || versionElement.GetString() != "2.0"
                || !root.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method)
            {
                await WriteJsonAsync(context.Response,
                    ErrorEnvelope(null, A2aErrorCodes.InvalidRequest, "invalid JSON-RPC request envelope"), 200);
                return;
            }
            id = root.TryGetProperty("id", out var idElement) ? idElement.GetRawText() : null;
            var parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : (JsonElement?)null;
            var outcome = await _rpc.DispatchAsync(method, parameters, _stop.Token);
            if (outcome is A2aDispatchOutcome.Stream stream)
            {
                await WriteSseAsync(context.Response, id, stream.Handle);
                return;
            }
            if (id is null)
            {
                context.Response.StatusCode = 204;
                context.Response.Close();
                return;
            }
            var result = outcome is A2aDispatchOutcome.Value { ResultJson: { } resultJson } ? resultJson : "null";
            await WriteJsonAsync(context.Response, ResultEnvelope(id, result), 200);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(context.Response, ErrorEnvelope(null, A2aErrorCodes.ParseError, "invalid JSON body"), 200);
        }
        catch (A2aException error)
        {
            await WriteJsonAsync(context.Response, ErrorEnvelope(id, error.Code, error.Message), 200);
        }
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private async Task WriteSseAsync(HttpListenerResponse response, string? id, A2aStreamHandle stream)
    {
        response.StatusCode = 200;
        response.ContentType = SseContentType;
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;
        foreach (var frame in stream.Backlog)
            await WriteSseFrameAsync(response, id, frame);
        if (stream.Live is not null)
        {
            await foreach (var frame in stream.Live.ReadAllAsync(_stop.Token))
                await WriteSseFrameAsync(response, id, frame);
        }
        response.Close();
    }

    private static async Task WriteSseFrameAsync(HttpListenerResponse response, string? id, string payloadJson)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {ResultEnvelope(id, payloadJson)}\n\n");
        await response.OutputStream.WriteAsync(bytes);
        await response.OutputStream.FlushAsync();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, string json, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentType = JsonContentType;
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static string ResultEnvelope(string? id, string payloadJson)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{id ?? "null"},\"result\":{payloadJson}}}";

    private static string ErrorEnvelope(string? id, int code, string message)
        => $"{{\"jsonrpc\":\"2.0\",\"id\":{id ?? "null"},\"error\":{{\"code\":{code},\"message\":{JsonSerializer.Serialize(message)}}}}}";
}
