using System.Text.Json;
using Dsh.Core;
using Dsh.Ide.Bridge;
using Dsh.Transport;
using static Dsh.RemoteHost.RemoteHostRpc;

namespace Dsh.RemoteHost;

/// <summary>Independent IDE domain plus the local half of the typed, cancellable extension protocol.</summary>
public sealed class RemoteEditorClient(RemoteHostClient host) : IAsyncDisposable, IRemoteEditors
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly EditorBridgeOperations _incoming = new(CancellationToken.None);
    private IEditorService? _local;
    private Func<EditorRequest, EditorRequest>? _map;
    private bool _registered;
    private Task? _retirement;
    private bool _disposed;

    public Task<EditorRemoteList> ListAsync(CancellationToken cancellationToken = default, bool refresh = false)
        => host.SendAsync(EditorProtocol.MethodList, Serialize(new RemoteEditorList(refresh), EditorProtocolJsonContext.Default.RemoteEditorList), EditorProtocolJsonContext.Default.EditorRemoteList, cancellationToken);
    public Task<EditorRemoteStatus> StatusAsync(string workspaceRoot, CancellationToken cancellationToken = default, string? sessionId = null)
        => host.SendAsync(EditorProtocol.MethodStatus, Serialize(new RemoteEditorWorkspace(workspaceRoot, sessionId), EditorProtocolJsonContext.Default.RemoteEditorWorkspace), EditorProtocolJsonContext.Default.EditorRemoteStatus, cancellationToken);
    public Task<IReadOnlyList<EditorDiagnostic>> DoctorAsync(string workspaceRoot, CancellationToken cancellationToken = default)
        => host.SendAsync(EditorProtocol.MethodDoctor, Serialize(new RemoteEditorWorkspace(workspaceRoot), EditorProtocolJsonContext.Default.RemoteEditorWorkspace), EditorProtocolJsonContext.Default.IReadOnlyListEditorDiagnostic, cancellationToken);

    public async Task<EditorResult> OpenAsync(EditorRequest request, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        try
        {
            return await host.SendAsync(EditorProtocol.MethodOpen, Serialize(new RemoteEditorOpen(id, request), EditorProtocolJsonContext.Default.RemoteEditorOpen), EditorProtocolJsonContext.Default.EditorResult, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!host.IsClosed)
            {
                try { await CancelAsync(id).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            throw;
        }
    }

    private Task<bool> CancelAsync(Guid id)
        => host.SendAsync(EditorProtocol.MethodCancel, Serialize(new RemoteEditorCancel(id), EditorProtocolJsonContext.Default.RemoteEditorCancel), EditorProtocolJsonContext.Default.Boolean, _lifetime.Token);

    /// <summary>The mapping is trusted local configuration; absence never treats remote filesystem paths as local.</summary>
    public async Task<IAsyncDisposable> RegisterClientAsync(IEditorService localService, CancellationToken cancellationToken = default, Func<EditorRequest, EditorRequest>? map = null, bool isLocalMachine = false, bool hasMappings = false)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_registered) throw new InvalidOperationException("An IDE handler is already registered on this connection.");
            _registered = true;
            _local = localService;
            _map = map;
        }
        try
        {
            await host.SendAsync(EditorProtocol.MethodRegister, Serialize(new RemoteEditorClientRegistration(IsLocalMachine: isLocalMachine, HasMappings: hasMappings), EditorProtocolJsonContext.Default.RemoteEditorClientRegistration), EditorProtocolJsonContext.Default.Boolean, cancellationToken).ConfigureAwait(false);
            return new Registration(this);
        }
        catch
        {
            lock (_gate) { _registered = false; _local = null; _map = null; }
            if (!host.IsClosed)
            {
                try { await UnregisterAsync().ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            throw;
        }
    }

    public Task<bool> SelectForSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => host.SendAsync(EditorProtocol.MethodSelect, Serialize(new RemoteEditorSelect(sessionId), EditorProtocolJsonContext.Default.RemoteEditorSelect), EditorProtocolJsonContext.Default.Boolean, cancellationToken);

    internal ValueTask<JsonElement?>? Prepare(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        if (!host.Hello.Ok) return null;
        if (message.Method == EditorProtocol.MethodClientOpen)
        {
            var request = message.Params is { } value ? JsonSerializer.Deserialize(value, EditorProtocolJsonContext.Default.RemoteEditorOpen) : null;
            if (request is null) throw new JsonRpcException(-32602, "client.ide.open requires a typed request.");
            return ResultAsync(_incoming.Start(request.OperationId, ct => LaunchLocalAsync(request.Request, ct, cancellationToken)));
        }
        if (message.Method == EditorProtocol.MethodClientCancel)
        {
            var request = message.Params is { } value ? JsonSerializer.Deserialize(value, EditorProtocolJsonContext.Default.RemoteEditorCancel) : null;
            if (request is null) throw new JsonRpcException(-32602, "client.ide.cancel requires operationId.");
            return AcknowledgeAsync(_incoming.CancelAsync(request.OperationId));
        }
        return null;
    }

    private async ValueTask<EditorResult> LaunchLocalAsync(EditorRequest request, CancellationToken cancellationToken, CancellationToken connectionClosed)
    {
        IEditorService? local;
        Func<EditorRequest, EditorRequest>? map;
        lock (_gate) { local = _local; map = _map; }
        if (local is null)
            return new EditorResult { Error = EditorErrorCode.CLIENT_UNAVAILABLE, Message = "No local IDE handler is registered." };
        if (map is null)
            return new EditorResult { Error = EditorErrorCode.UNSUPPORTED, Message = "A locally trusted remote workspace mapping is required." };
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectionClosed, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        try
        {
            var mapped = map(request);
            // A remote request cannot select a locally detected host editor or redirect execution again.
            mapped = mapped with { ExecutionTarget = EditorExecutionTarget.Local, HostEditor = null, RememberSelection = false, MachineId = null };
            return await local.LaunchAsync(mapped, linked.Token).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            return new EditorResult { Error = EditorErrorCode.OUTSIDE_WORKSPACE, Message = "The request does not match a trusted workspace mapping." };
        }
        catch (NotSupportedException)
        {
            return new EditorResult { Error = EditorErrorCode.UNSUPPORTED, Message = "The selected editor does not support this remote mapping." };
        }
    }

    private static async ValueTask<JsonElement?> ResultAsync(Task<EditorResult> operation)
        => Serialize(await operation.ConfigureAwait(false), EditorProtocolJsonContext.Default.EditorResult);
    private static async ValueTask<JsonElement?> AcknowledgeAsync(Task operation)
    {
        await operation.ConfigureAwait(false);
        return Serialize(true, EditorProtocolJsonContext.Default.Boolean);
    }
    private async ValueTask UnregisterAsync()
    {
        lock (_gate) { _registered = false; _local = null; _map = null; }
        if (!host.IsClosed)
            await host.SendAsync(EditorProtocol.MethodUnregister, null, EditorProtocolJsonContext.Default.Boolean, _lifetime.Token).ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_retirement ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        lock (_gate)
        {
            _disposed = true;
            _registered = false;
            _local = null;
            _map = null;
        }
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _incoming.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
    private sealed class Registration(RemoteEditorClient owner) : IAsyncDisposable
    {
        private int _disposed;
        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.UnregisterAsync() : ValueTask.CompletedTask;
    }
}
