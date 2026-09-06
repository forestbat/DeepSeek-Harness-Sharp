using System.Collections.Concurrent;
using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Sdk;

public delegate Task<object?> JsonRpcRequestHandler(string method, JsonElement? parameters);
public delegate void JsonRpcNotificationHandler(string method, JsonElement? parameters);

public interface IJsonRpcPeer
{
    Task<object?> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default);
    void Notify(string method, object? parameters = null);
}

public sealed class JsonRpcLineTransport : IJsonRpcPeer, IAsyncDisposable
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _pending = new();
    private readonly Lock _writeLock = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _readCancellation;
    private Task? _readLoop;
    private bool _started;

    public JsonRpcLineTransport(TextReader input, TextWriter output)
    {
        _input = input;
        _output = output;
    }

    public JsonRpcRequestHandler? RequestHandler { get; set; }
    public JsonRpcNotificationHandler? NotificationHandler { get; set; }

    public void Start()
    {
        if (_started)
            return;
        _started = true;
        _readCancellation = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_readCancellation.Token));
    }

    public Task WhenClosedAsync() => _closed.Task;

    public async Task StopAsync()
    {
        if (!_started)
            return;
        _started = false;
        _readCancellation?.Cancel();
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        FailPending(new JsonRpcTransportClosedException("JSON-RPC transport closed"));
        _readCancellation?.Dispose();
        _readCancellation = null;
    }

    public async Task<object?> RequestAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        var id = $"req_{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var registration = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var pending))
                pending.TrySetCanceled(cancellationToken);
        });
        try
        {
            WriteFrame(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new Dictionary<string, object?>(),
            });
            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public void Notify(string method, object? parameters = null)
    {
        var frame = parameters is null
            ? new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method }
            : new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters };
        WriteFrame(frame);
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                HandleLine(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            FailPending(new JsonRpcTransportClosedException($"JSON-RPC input failed: {error.Message}"));
        }
        finally
        {
            FailPending(new JsonRpcTransportClosedException("JSON-RPC input closed"));
            _closed.TrySetResult();
        }
    }

    private void HandleLine(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return;
            if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
            {
                if (root.TryGetProperty("id", out var responseId))
                    HandleIncomingResponse(responseId, root);
                return;
            }

            var method = methodElement.GetString()!;
            JsonElement? parameters = root.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : null;
            if (root.TryGetProperty("id", out var requestId))
            {
                _ = HandleIncomingRequestAsync(requestId, method, parameters);
                return;
            }
            NotificationHandler?.Invoke(method, parameters);
        }
    }

    private async Task HandleIncomingRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        var handler = RequestHandler;
        if (handler is null)
        {
            WriteError(id, -32601, $"method not found: {method}");
            return;
        }
        try
        {
            var result = await handler(method, parameters).ConfigureAwait(false);
            WriteFrame(new Dictionary<string, object?>
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.Clone(),
                ["result"] = result,
            });
        }
        catch (Exception error)
        {
            WriteError(id, -32603, error.Message);
        }
    }

    private void HandleIncomingResponse(JsonElement id, JsonElement frame)
    {
        var idText = id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : id.ValueKind == JsonValueKind.Number ? id.GetRawText() : null;
        if (idText is null || !_pending.TryRemove(idText, out var pending))
            return;
        if (frame.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
        {
            var code = errorElement.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.Number
                ? codeElement.GetInt32()
                : (int?)null;
            var message = errorElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()!
                : "JSON-RPC error";
            pending.TrySetException(new JsonRpcResponseError(code, message,
                errorElement.TryGetProperty("data", out var dataElement) ? dataElement.Clone() : null));
            return;
        }
        pending.TrySetResult(frame.TryGetProperty("result", out var resultElement) ? resultElement.Clone() : null);
    }

    private void WriteFrame(IReadOnlyDictionary<string, object?> frame)
    {
        var line = JsonSerializer.Serialize(frame, DshJson.Options);
        lock (_writeLock)
        {
            _output.WriteLine(line);
            _output.Flush();
        }
    }

    private void WriteError(JsonElement id, int code, string message)
    {
        WriteFrame(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.Clone(),
            ["error"] = new Dictionary<string, object?>
            {
                ["code"] = code,
                ["message"] = message,
            },
        });
    }

    private void FailPending(Exception error)
    {
        foreach (var pending in _pending)
        {
            if (_pending.TryRemove(pending.Key, out var completion))
                completion.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class JsonRpcResponseError : Exception
{
    public JsonRpcResponseError(int? code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        DataValue = data;
    }

    public int? Code { get; }
    public JsonElement? DataValue { get; }
}

public sealed class JsonRpcTransportClosedException : Exception
{
    public JsonRpcTransportClosedException(string message) : base(message)
    {
    }
}
