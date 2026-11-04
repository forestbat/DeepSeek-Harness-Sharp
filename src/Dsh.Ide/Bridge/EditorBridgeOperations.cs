using Dsh.Core;

namespace Dsh.Ide.Bridge;

/// <summary>Owns incoming IDE operations in frame order and drains cancellation before retirement.</summary>
public sealed class EditorBridgeOperations(CancellationToken lifetime) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Operation> _operations = [];
    private bool _disposed;

    public Task<EditorResult> Start(Guid id, Func<CancellationToken, ValueTask<EditorResult>> handler)
    {
        Operation operation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (id == Guid.Empty || _operations.ContainsKey(id))
                throw new ArgumentException("IDE operationId must be nonempty and unique on this connection.");
            operation = new Operation(CancellationTokenSource.CreateLinkedTokenSource(lifetime));
            _operations.Add(id, operation);
        }
        return RunAsync(id, operation, handler);
    }

    private async Task<EditorResult> RunAsync(Guid id, Operation operation, Func<CancellationToken, ValueTask<EditorResult>> handler)
    {
        await Task.Yield();
        try
        {
            operation.Source.Token.ThrowIfCancellationRequested();
            return await handler(operation.Source.Token).ConfigureAwait(false);
        }
        finally
        {
            Task cancellation;
            lock (_gate)
            {
                operation.Finishing = true;
                cancellation = operation.Cancellation ?? Task.CompletedTask;
            }
            try { await cancellation.ConfigureAwait(false); }
            finally
            {
                lock (_gate)
                {
                    operation.Source.Dispose();
                    _operations.Remove(id);
                    operation.Completion.TrySetResult();
                }
            }
        }
    }

    public Task CancelAsync(Guid id)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(id, out var operation)) return Task.CompletedTask;
            if (!operation.Finishing) operation.Cancellation ??= operation.Source.CancelAsync();
            return AwaitRetirementAsync(operation);
        }
    }

    private static async Task AwaitRetirementAsync(Operation operation)
    {
        try { await (operation.Cancellation ?? Task.CompletedTask).ConfigureAwait(false); }
        finally { await operation.Completion.Task.ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _operations.Values.Select(operation =>
            {
                if (!operation.Finishing) operation.Cancellation ??= operation.Source.CancelAsync();
                return AwaitRetirementAsync(operation);
            }).ToArray();
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
    }

    private sealed class Operation(CancellationTokenSource source)
    {
        public CancellationTokenSource Source { get; } = source;
        public Task? Cancellation { get; set; }
        public bool Finishing { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
