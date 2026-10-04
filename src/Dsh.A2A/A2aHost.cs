using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using A2A;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dsh.A2A;

/**
 * 自备 A2A 宿主: 核心 A2A SDK 的 A2AServer 承载协议语义, 这里只做 HTTP 路由(well-known card + JSON-RPC)
 * 与 SSE 帧。刻意不引 A2A.AspNetCore, 以免宿主整体依赖 ASP.NET Core 共享框架。
 */
public sealed class A2aHost : IDisposable
{
    private const string CardPath = "/.well-known/agent-card.json";
    private static readonly JsonSerializerOptions Json = A2AJsonUtilities.DefaultOptions;

    private readonly A2AServer _server;
    private readonly AgentCard _card;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    public A2aHost(DshAgentHandler handler, A2aHostOptions options)
    {
        var port = options.Port > 0 ? options.Port : ProbeFreePort();
        var host = string.IsNullOrWhiteSpace(options.Host) ? "127.0.0.1" : options.Host;
        Endpoint = $"http://{host}:{port}";
        _listener.Prefixes.Add($"{Endpoint}/");
        _card = BuildCard(options.PublicUrl ?? Endpoint, options);
        _server = new A2AServer(handler, new InMemoryTaskStore(), new ChannelEventNotifier(), NullLogger<A2AServer>.Instance);
    }

    public string Endpoint { get; }

    public void Start()
    {
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception)
        {
        }
        _server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                break;
            }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var path = request.Url?.AbsolutePath ?? "";
            if (request.HttpMethod == "GET" && path.EndsWith(CardPath, StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(context.Response, _card);
                return;
            }
            if (request.HttpMethod == "POST")
            {
                using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                var body = await reader.ReadToEndAsync();
                JsonRpcRequest? rpc;
                try
                {
                    rpc = JsonSerializer.Deserialize<JsonRpcRequest>(body, Json);
                }
                catch (JsonException)
                {
                    await WriteJsonAsync(context.Response, JsonRpcResponse.ParseErrorResponse(default));
                    return;
                }
                if (rpc is null)
                {
                    await WriteJsonAsync(context.Response, JsonRpcResponse.ParseErrorResponse(default));
                    return;
                }
                await DispatchAsync(context, rpc, _stop.Token);
                return;
            }
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            context.Response.Close();
        }
        catch (Exception)
        {
            TryFail(context.Response);
        }
    }

    private async Task DispatchAsync(HttpListenerContext context, JsonRpcRequest rpc, CancellationToken cancellationToken)
    {
        try
        {
            switch (rpc.Method)
            {
                case A2AMethods.SendMessage:
                    await WriteJsonAsync(context.Response, JsonRpcResponse.CreateJsonRpcResponse(rpc.Id,
                        await _server.SendMessageAsync(Require<SendMessageRequest>(rpc), cancellationToken)));
                    return;
                case A2AMethods.GetTask:
                    await WriteJsonAsync(context.Response, JsonRpcResponse.CreateJsonRpcResponse(rpc.Id,
                        await _server.GetTaskAsync(Require<GetTaskRequest>(rpc), cancellationToken)));
                    return;
                case A2AMethods.ListTasks:
                    await WriteJsonAsync(context.Response, JsonRpcResponse.CreateJsonRpcResponse(rpc.Id,
                        await _server.ListTasksAsync(Require<ListTasksRequest>(rpc), cancellationToken)));
                    return;
                case A2AMethods.CancelTask:
                    await WriteJsonAsync(context.Response, JsonRpcResponse.CreateJsonRpcResponse(rpc.Id,
                        await _server.CancelTaskAsync(Require<CancelTaskRequest>(rpc), cancellationToken)));
                    return;
                case A2AMethods.SendStreamingMessage:
                    await WriteStreamAsync(context.Response, rpc.Id, _server.SendStreamingMessageAsync(Require<SendMessageRequest>(rpc), cancellationToken), cancellationToken);
                    return;
                case A2AMethods.SubscribeToTask:
                    await WriteStreamAsync(context.Response, rpc.Id, _server.SubscribeToTaskAsync(Require<SubscribeToTaskRequest>(rpc), cancellationToken), cancellationToken);
                    return;
                default:
                    await WriteJsonAsync(context.Response, JsonRpcResponse.MethodNotFoundResponse(rpc.Id, $"unknown method: {rpc.Method}"));
                    return;
            }
        }
        catch (A2AException error)
        {
            await WriteJsonAsync(context.Response, JsonRpcResponse.CreateJsonRpcErrorResponse(rpc.Id, error));
        }
        catch (Exception error)
        {
            await WriteJsonAsync(context.Response, JsonRpcResponse.InternalErrorResponse(rpc.Id, error.Message));
        }
    }

    private static T Require<T>(JsonRpcRequest rpc)
    {
        if (rpc.Params is not { } parameters)
            throw new A2AException($"method {rpc.Method} requires params", A2AErrorCode.InvalidParams);
        return parameters.Deserialize<T>(Json)
            ?? throw new A2AException($"invalid params for {rpc.Method}", A2AErrorCode.InvalidParams);
    }

    private static async Task WriteStreamAsync(HttpListenerResponse response, JsonRpcId id, IAsyncEnumerable<StreamResponse> events, CancellationToken cancellationToken)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        await using var writer = new StreamWriter(response.OutputStream, new UTF8Encoding(false));
        await foreach (var streamEvent in events.WithCancellation(cancellationToken))
        {
            var frame = JsonRpcResponse.CreateJsonRpcResponse(id, streamEvent);
            await writer.WriteAsync($"data: {JsonSerializer.Serialize(frame, Json)}\n\n");
            await writer.FlushAsync();
        }
        response.Close();
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static void TryFail(HttpListenerResponse response)
    {
        try
        {
            response.StatusCode = (int)HttpStatusCode.InternalServerError;
            response.Close();
        }
        catch (Exception)
        {
        }
    }

    private static int ProbeFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static AgentCard BuildCard(string url, A2aHostOptions options)
    {
        var skill = options.Skill;
        return new AgentCard
        {
            Name = "deepseek-harness",
            Description = "DeepSeek Harness agent exposed over the A2A protocol",
            Version = typeof(A2aHost).Assembly.GetName().Version?.ToString() ?? "0.0.1",
            SupportedInterfaces = [new AgentInterface { Url = url, ProtocolBinding = "JSONRPC", ProtocolVersion = "1.0" }],
            Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = false },
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = skill.Id, Name = skill.Name, Description = skill.Description, Tags = [.. skill.EffectiveTags] }],
        };
    }
}
