using System.Text.Json;
using Dsh.Llm;

namespace Dsh.Sdk;

public sealed class HarnessClient : IAsyncDisposable
{
    private readonly JsonRpcLineTransport _transport;
    private readonly List<NotificationSubscription> _subscriptions = [];
    private bool _closed;

    public HarnessClient(JsonRpcLineTransport transport)
    {
        _transport = transport;
        _transport.NotificationHandler = DispatchNotification;
    }

    public async Task<InitializeResult> InitializeAsync(InitializeParams parameters)
    {
        var result = await RequestAsync(SdkMethods.Initialize, parameters);
        return DeserializeResult<InitializeResult>(result, "initialize");
    }

    public async Task<string> PromptAsync(string sessionId, IReadOnlyList<ContentBlock> contentBlocks)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["contentBlocks"] = contentBlocks,
        };
        var result = await RequestAsync(SdkMethods.SessionPrompt, parameters);
        var promptResult = DeserializeResult<SessionPromptResult>(result, "session/prompt");
        return promptResult.MessageId;
    }

    public async Task<object?> RequestAsync(string method, object? parameters = null)
    {
        if (_closed)
            throw new InvalidOperationException("DeepSeek Harness SDK client is closed");
        return await _transport.RequestAsync(method, parameters);
    }

    public NotificationSubscription Subscribe(Func<string, JsonElement?, bool>? filter = null)
    {
        lock (_subscriptions)
        {
            var subscription = new NotificationSubscription(filter);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    public async Task CloseAsync()
    {
        if (_closed)
            return;
        _closed = true;
        await _transport.StopAsync();
        lock (_subscriptions)
        {
            foreach (var subscription in _subscriptions)
                subscription.Fail(new InvalidOperationException("DeepSeek Harness SDK client closed"));
            _subscriptions.Clear();
        }
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    private void DispatchNotification(string method, JsonElement? parameters)
    {
        NotificationSubscription[] snapshot;
        lock (_subscriptions)
            snapshot = [.._subscriptions];
        foreach (var subscription in snapshot)
            subscription.Push(method, parameters);
    }

    private static T DeserializeResult<T>(object? result, string method) where T : class
    {
        if (result is not JsonElement element)
            throw new InvalidOperationException($"{method} returned no JSON result");
        return element.Deserialize<T>(DshJson.Options)
            ?? throw new InvalidOperationException($"{method} returned an invalid result");
    }
}

public sealed class NotificationSubscription : IDisposable
{
    private readonly Func<string, JsonElement?, bool>? _filter;
    private readonly Queue<(string Method, JsonElement? Parameters)> _queue = [];
    private readonly Queue<TaskCompletionSource<(string Method, JsonElement? Parameters)>> _waiters = [];
    private readonly Lock _sync = new();
    private Exception? _failure;
    private bool _disposed;

    public NotificationSubscription(Func<string, JsonElement?, bool>? filter = null)
    {
        _filter = filter;
    }

    public async Task<(string Method, JsonElement? Parameters)> NextAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<(string Method, JsonElement? Parameters)>? waiter;
        lock (_sync)
        {
            if (_queue.Count > 0)
                return _queue.Dequeue();
            if (_failure is not null)
                throw _failure;
            if (_disposed)
                throw new InvalidOperationException("notification subscription closed");
            waiter = new TaskCompletionSource<(string Method, JsonElement? Parameters)>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Enqueue(waiter);
        }
        try
        {
            using var registration = cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            return await waiter.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                if (_waiters.TryPeek(out var head) && ReferenceEquals(head, waiter))
                    _waiters.Dequeue();
            }
        }
    }

    public bool TryNext(out string method, out JsonElement? parameters)
    {
        lock (_sync)
        {
            if (_queue.Count > 0)
            {
                var (queuedMethod, queuedParameters) = _queue.Dequeue();
                method = queuedMethod;
                parameters = queuedParameters;
                return true;
            }
            method = "";
            parameters = null;
            return false;
        }
    }

    public void Push(string method, JsonElement? parameters)
    {
        if (_filter is not null)
        {
            bool matches;
            try
            {
                matches = _filter(method, parameters);
            }
            catch (Exception error)
            {
                Fail(error);
                return;
            }
            if (!matches)
                return;
        }

        lock (_sync)
        {
            if (_disposed)
                return;
            if (_waiters.Count > 0)
            {
                var waiter = _waiters.Dequeue();
                waiter.TrySetResult((method, parameters));
            }
            else
            {
                _queue.Enqueue((method, parameters));
            }
        }
    }

    public void Fail(Exception error)
    {
        lock (_sync)
        {
            _failure ??= error;
            while (_waiters.Count > 0)
                _waiters.Dequeue().TrySetException(_failure);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _queue.Clear();
            while (_waiters.Count > 0)
                _waiters.Dequeue().TrySetException(new InvalidOperationException("notification subscription closed"));
        }
    }
}
