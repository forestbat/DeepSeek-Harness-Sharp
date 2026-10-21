using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Lsp.Protocol;

public sealed class LspProtocolException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class LspResponseException(int? code, string message, JsonElement? data = null)
    : Exception(message)
{
    public int? Code { get; } = code;
    public JsonElement? ErrorData { get; } = data;
}

public sealed class LspConnectionClosedException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class LspRequestTimeoutException(string method)
    : TimeoutException($"LSP request '{method}' timed out");

public static class LspFrameCodec
{
    public const int MaxHeaderBytes = 16 * 1024;
    public const int MaxContentBytes = 32 * 1024 * 1024;

    public static async Task WriteAsync(Stream stream, JsonObject message, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<JsonElement?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var contentLength = -1;
        var headerBytes = 0;
        while (true)
        {
            var line = await ReadHeaderLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (line is null)
                return null;
            headerBytes += line.Length + 2;
            if (headerBytes > MaxHeaderBytes)
                throw new LspProtocolException($"LSP header exceeds {MaxHeaderBytes} bytes");
            if (line.Length == 0)
                break;
            var separator = line.IndexOf(':');
            if (separator <= 0)
                continue;
            var name = line[..separator].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line[(separator + 1)..].Trim(), CultureInfo.InvariantCulture, out var parsed))
                contentLength = parsed;
        }

        if (contentLength < 0)
            throw new LspProtocolException("LSP message has no Content-Length header");
        if (contentLength > MaxContentBytes)
            throw new LspProtocolException($"LSP message exceeds {MaxContentBytes} bytes");

        var body = new byte[contentLength];
        await ReadExactlyAsync(stream, body, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static async Task<string?> ReadHeaderLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(64);
        var single = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (bytes.Count == 0)
                    return null;
                throw new LspProtocolException("LSP stream ended in the middle of a header");
            }

            if (single[0] == (byte)'\n')
                return Encoding.ASCII.GetString([.. bytes]);
            if (single[0] != (byte)'\r')
                bytes.Add(single[0]);
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new LspProtocolException("LSP stream ended before the message body was complete");
            offset += read;
        }
    }
}

public delegate Task<JsonElement?> LspServerRequestHandler(
    string method,
    JsonElement? parameters,
    CancellationToken cancellationToken);

/**
 * Framed JSON-RPC connection used by the LSP wire protocol.
 * It owns request correlation and replies to server-initiated requests that keep initialization alive.
 */
public sealed class LspMessageConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly bool _ownsStreams;
    private readonly Process? _process;
    private readonly TimeSpan _requestTimeout;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource<Exception?> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _readLoop;
    private long _nextId;
    private int _disposed;

    public LspMessageConnection(
        Stream input,
        Stream output,
        bool ownsStreams = false,
        Process? process = null,
        TimeSpan? requestTimeout = null)
    {
        _input = input;
        _output = output;
        _ownsStreams = ownsStreams;
        _process = process;
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(30);
        ConnectionClosed = _lifetime.Token;
    }

    public event Action<string, JsonElement?>? Notification;

    public LspServerRequestHandler? ServerRequestHandler { get; set; }

    public CancellationToken ConnectionClosed { get; }

    public Task<Exception?> Closed => _closed.Task;

    public void Start()
    {
        _readLoop ??= Task.Run(() => ReadLoopAsync(_lifetime.Token));
    }

    public async Task<JsonElement?> RequestAsync(
        string method,
        JsonNode? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var cancellation = cancellationToken.Register(() => _ = CancelAsync(id));
        try
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters?.DeepClone(),
            }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(_requestTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
        {
            _ = CancelAsync(id);
            throw new LspRequestTimeoutException(method);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters = null, CancellationToken cancellationToken = default)
        => WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters?.DeepClone(),
        }, cancellationToken);

    private async Task CancelAsync(long id)
    {
        try
        {
            await NotifyAsync("$/cancelRequest", new JsonObject { ["id"] = id }, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _ = error;
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await LspFrameCodec.ReadAsync(_input, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    break;
                Dispatch(frame.Value);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            Complete(failure);
        }
    }

    private void Dispatch(JsonElement frame)
    {
        if (frame.ValueKind != JsonValueKind.Object)
            return;
        if (frame.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
        {
            var method = methodElement.GetString()!;
            var parameters = frame.TryGetProperty("params", out var paramsElement) ? paramsElement.Clone() : (JsonElement?)null;
            if (frame.TryGetProperty("id", out var requestId))
            {
                _ = HandleServerRequestAsync(requestId.Clone(), method, parameters);
                return;
            }

            Notification?.Invoke(method, parameters);
            return;
        }

        if (!frame.TryGetProperty("id", out var idElement) || !TryReadId(idElement, out var id))
            return;
        if (!_pending.TryRemove(id, out var completion))
            return;
        if (frame.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            completion.TrySetException(new LspResponseException(
                LspJson.Int32(error, "code"),
                LspJson.String(error, "message") ?? "LSP response error",
                LspJson.Property(error, "data")));
            return;
        }

        completion.TrySetResult(frame.TryGetProperty("result", out var result) ? result.Clone() : null);
    }

    private async Task HandleServerRequestAsync(JsonElement id, string method, JsonElement? parameters)
    {
        try
        {
            var handler = ServerRequestHandler;
            var result = handler is null ? null : await handler(method, parameters, _lifetime.Token).ConfigureAwait(false);
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["result"] = result is null ? null : JsonNode.Parse(result.Value.GetRawText()),
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await TryWriteErrorAsync(id, error).ConfigureAwait(false);
        }
    }

    private async Task TryWriteErrorAsync(JsonElement id, Exception error)
    {
        try
        {
            var code = error is LspResponseException response ? response.Code ?? -32603 : -32603;
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["error"] = new JsonObject
                {
                    ["code"] = code,
                    ["message"] = error.Message,
                },
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            Complete(error);
        }
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_closed.Task.IsCompleted)
                throw new LspConnectionClosedException("LSP connection is closed");
            await LspFrameCodec.WriteAsync(_output, message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Complete(error);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static bool TryReadId(JsonElement element, out long id)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out id))
            return true;
        id = 0;
        return false;
    }

    private void Complete(Exception? failure)
    {
        if (!_closed.TrySetResult(failure))
            return;
        _lifetime.Cancel();
        var error = failure ?? new LspConnectionClosedException("LSP connection closed");
        foreach (var pending in _pending)
        {
            if (_pending.TryRemove(pending.Key, out var completion))
                completion.TrySetException(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Complete(null);
        if (_ownsStreams)
        {
            await _input.DisposeAsync().ConfigureAwait(false);
            await _output.DisposeAsync().ConfigureAwait(false);
        }

        if (_readLoop is { } readLoop)
        {
            try
            {
                await readLoop.ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _ = error;
            }
        }

        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (Exception error)
            {
                _ = error;
            }
        }

        _process?.Dispose();
        _lifetime.Dispose();
        _writeGate.Dispose();
    }
}
