using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cordis.Node;

public sealed class RpcConnection : IDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();

    public Func<JsonNode, Task<JsonNode?>>? OnRequest { get; set; }
    public Action<JsonNode>? OnNotification { get; set; }

    public RpcConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
        _ = Task.Run(ReadLoop);
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? @params, CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null) message["params"] = @params;
        await WriteMessage(message);
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        var response = await tcs.Task;
        if (response?["error"] is { } error)
        {
            throw new JsRemoteException(
                error["message"]?.GetValue<string>() ?? "unknown remote error",
                error["stack"]?.GetValue<string>());
        }
        return response?["result"];
    }

    public async Task NotifyAsync(string method, JsonNode? @params)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (@params is not null) message["params"] = @params;
        await WriteMessage(message);
    }

    private async Task WriteMessage(JsonObject message)
    {
        var line = message.ToJsonString() + "\n";
        var bytes = System.Text.Encoding.UTF8.GetBytes(line);
        await _writeLock.WaitAsync();
        try
        {
            await _output.WriteAsync(bytes);
            await _output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoop()
    {
        var reader = new StreamReader(_input);
        while (!_disposeCts.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(_disposeCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                break;
            }
            if (line is null) break;
            if (line.Length == 0) continue;
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch
            {
                continue;
            }
            if (message is null) continue;
            _ = Task.Run(() => Dispatch(message));
        }
        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetException(new JsRemoteException("connection closed", null));
        }
    }

    private async Task Dispatch(JsonNode message)
    {
        var idNode = message["id"];
        var method = message["method"]?.GetValue<string>();
        if (idNode is not null && method is null)
        {
            var id = idNode.GetValue<long>();
            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(message);
            }
            return;
        }
        if (method is null) return;
        if (idNode is null)
        {
            OnNotification?.Invoke(message);
            return;
        }
        var requestId = idNode.GetValue<long>();
        JsonNode? result = null;
        JsonNode? error = null;
        try
        {
            if (OnRequest is null) throw new JsRemoteException($"no handler for {method}", null);
            result = await OnRequest(message);
        }
        catch (Exception exception)
        {
            error = new JsonObject
            {
                ["message"] = exception.Message,
                ["stack"] = exception.ToString(),
            };
        }
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = requestId,
        };
        if (error is not null) response["error"] = error;
        else response["result"] = result;
        await WriteMessage(response);
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }
}

public sealed class JsRemoteException : Exception
{
    public string? RemoteStack { get; }

    public JsRemoteException(string message, string? stack) : base(message)
    {
        RemoteStack = stack;
    }
}
