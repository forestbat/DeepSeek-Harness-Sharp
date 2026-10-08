using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace Dsh.Transport;

/** 远端回了一个 JSON-RPC error, 或本端调用失败。 */
public sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message, JsonElement? data = null)
        : base(message)
    {
        Code = code;
        ErrorData = data;
    }

    public int Code { get; }

    public JsonElement? ErrorData { get; }
}

/**
 * 在一对长度帧流上跑 JSON-RPC 2.0: 请求/响应按 id 配对, 通知单向派发, 入向请求交 RequestHandler。
 * 读由单条读循环串行化; 写由 _writeGate 串行化, 避免并发写撕裂帧。
 * 中立层: ACP 重写与 §14 远程工作区都以它为基础, 互不依赖。
 */
public sealed class JsonRpcPeer : IAsyncDisposable
{
    /** JSON-RPC 标准错误码: 方法不存在。 */
    public const int MethodNotFound = -32601;

    /** JSON-RPC 标准错误码: 服务端内部错误。 */
    public const int InternalError = -32603;

    private static readonly JsonElement JsonNull = JsonDocument.Parse("null").RootElement.Clone();

    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcMessage>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private long _nextId;
    private int _disposed;
    private Task? _readLoop;

    public JsonRpcPeer(Stream stream, int maxFrameBytes = FrameCodec.DefaultMaxFrameBytes)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _maxFrameBytes = maxFrameBytes;
    }

    /** 入向请求处理: 返回 result 原样回填; 抛 JsonRpcException 回该 error; null 表示不支持(回 method not found)。 */
    public Func<JsonRpcMessage, CancellationToken, ValueTask<JsonElement?>>? RequestHandler { get; set; }

    /** 入向通知(无需响应)。 */
    public event Action<JsonRpcMessage>? NotificationReceived;

    /** 读循环结束(正常 EOF 时 failure=null; 异常时带上原因)。 */
    public event Action<Exception?>? Closed;

    public void Start() => _readLoop ??= Task.Run(() => ReadLoopAsync(_cts.Token));

    public bool IsClosed => _readLoop is { IsCompleted: true };

    public async Task<JsonElement?> RequestAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        var id = Interlocked.Increment(ref _nextId);
        var key = id.ToString(CultureInfo.InvariantCulture);
        var message = new JsonRpcMessage
        {
            Id = JsonSerializer.SerializeToElement(id, JsonRpcJsonContext.Default.Int64),
            Method = method,
            Params = parameters,
        };

        var completion = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = completion;
        try
        {
            await SendAsync(message, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var response = await completion.Task.ConfigureAwait(false);
            if (response.Error is { } error)
                throw new JsonRpcException(error.Code, error.Message, error.Data);
            return response.Result;
        }
        catch
        {
            _pending.TryRemove(key, out _);
            throw;
        }
    }

    public Task NotifyAsync(string method, JsonElement? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(method);
        return SendAsync(new JsonRpcMessage { Method = method, Params = parameters }, cancellationToken);
    }

    private async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, JsonRpcJsonContext.Default.JsonRpcMessage);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteFrameAsync(_stream, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await FrameCodec.ReadFrameAsync(_stream, _maxFrameBytes, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                    break;

                JsonRpcMessage message;
                try
                {
                    message = JsonSerializer.Deserialize(frame, JsonRpcJsonContext.Default.JsonRpcMessage)
                        ?? throw new IOException("空 JSON-RPC 报文");
                }
                catch (JsonException error)
                {
                    failure = error;
                    break;
                }

                await DispatchAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            FailPending(failure);
            Closed?.Invoke(failure);
        }
    }

    private async ValueTask DispatchAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        if (message.IsResponse)
        {
            if (message.IdKey is { } key && _pending.TryRemove(key, out var completion))
                completion.TrySetResult(message);
            return;
        }

        if (message.IsNotification)
        {
            NotificationReceived?.Invoke(message);
            return;
        }

        if (message.IsRequest && message.Id is { } requestId)
        {
            var response = new JsonRpcMessage { Id = requestId };
            try
            {
                var handler = RequestHandler;
                response.Result = handler is null
                    ? throw new JsonRpcException(MethodNotFound, $"method not found: {message.Method}")
                    : await handler(message, cancellationToken).ConfigureAwait(false) ?? JsonNull;
            }
            catch (JsonRpcException error)
            {
                response.Result = null;
                response.Error = new JsonRpcError { Code = error.Code, Message = error.Message, Data = error.ErrorData };
            }
            catch (Exception error)
            {
                response.Result = null;
                response.Error = new JsonRpcError { Code = InternalError, Message = error.Message };
            }

            await SendAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private void FailPending(Exception? failure)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
                completion.TrySetException(failure ?? new IOException("连接已关闭"));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _cts.Cancel();
        // 关闭底层流以打断阻塞中的读(Socket/管道读未必及时响应 token 取消)。
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 关流失败不影响收尾。
        }

        if (_readLoop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        FailPending(null);
        _writeGate.Dispose();
        _cts.Dispose();
    }
}
