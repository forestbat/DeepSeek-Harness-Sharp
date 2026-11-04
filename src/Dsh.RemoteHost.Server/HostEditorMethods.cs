using System.Text.Json;
using Dsh.Core;
using Dsh.Ide.Bridge;
using Dsh.Transport;
using static Dsh.RemoteHost.HostRpc;

namespace Dsh.RemoteHost;

internal sealed class HostEditorMethods(IRemoteEditorBackend backend, JsonRpcPeer peer, string clientId, CancellationToken lifetime) : IAsyncDisposable
{
    private readonly EditorBridgeOperations _operations = new(lifetime);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TaskCompletionSource> _reverseRequests = [];
    private IDisposable? _registration;
    private bool _disposed;

    public ValueTask<JsonElement?>? Prepare(string name, JsonRpcMessage message)
    {
        using var clientScope = backend.Clients?.EnterClientScope(clientId);
        if (name == EditorProtocol.MethodOpen)
        {
            var request = Require(message, EditorProtocolJsonContext.Default.RemoteEditorOpen, "ide.open requires operationId and a typed request");
            Validate(request.Request);
            var operation = _operations.Start(request.OperationId, ct => backend.Service.LaunchAsync(request.Request, ct));
            return ResultAsync(operation);
        }
        if (name == EditorProtocol.MethodCancel)
        {
            var request = Require(message, EditorProtocolJsonContext.Default.RemoteEditorCancel, "ide.cancel requires operationId");
            return AcknowledgeAsync(_operations.CancelAsync(request.OperationId));
        }
        if (name == EditorProtocol.MethodUnregister)
        {
            return AcknowledgeAsync(RetireClientAsync());
        }
        if (name == EditorProtocol.MethodRegister)
        {
            var request = Require(message, EditorProtocolJsonContext.Default.RemoteEditorClientRegistration, "ide.client.register requires protocolVersion");
            if (request.ProtocolVersion != EditorProtocol.Version)
                throw new JsonRpcException(InvalidParams, "IDE extension protocol version mismatch.");
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (backend.Clients is not { } clients)
                    throw MethodNotFound(name);
                _registration ??= clients.RegisterClient(clientId, OpenClientAsync, lifetime, request.IsLocalMachine, request.HasMappings);
            }
            return ValueTask.FromResult<JsonElement?>(True);
        }
        if (name == EditorProtocol.MethodSelect)
        {
            var request = Require(message, EditorProtocolJsonContext.Default.RemoteEditorSelect, "ide.client.select requires sessionId");
            lock (_gate)
            {
                if (_registration is null || backend.Clients is null)
                    throw new JsonRpcException(InvalidParams, "Register this connection's IDE handler before selecting it.");
                backend.Clients.SelectClient(request.SessionId, clientId);
            }
            return ValueTask.FromResult<JsonElement?>(True);
        }
        return null;
    }

    public async ValueTask<JsonElement?> HandleAsync(string name, JsonRpcMessage message, CancellationToken cancellationToken)
    {
        switch (name)
        {
            case EditorProtocol.MethodList:
            {
                var options = message.Params is null ? new RemoteEditorList() : Require(message, EditorProtocolJsonContext.Default.RemoteEditorList, "ide.list requires refresh as a boolean.");
                return Serialize(new EditorRemoteList(backend.Service.Catalog.Descriptors, await backend.Service.Catalog.DetectAsync(options.Refresh, cancellationToken).ConfigureAwait(false)), EditorProtocolJsonContext.Default.EditorRemoteList);
            }
            case EditorProtocol.MethodStatus:
            {
                var request = Require(message, EditorProtocolJsonContext.Default.RemoteEditorWorkspace, "ide.status requires workspaceRoot");
                var root = backend.Paths?.Resolve(request.WorkspaceRoot) ?? request.WorkspaceRoot;
                return Serialize(new EditorRemoteStatus(backend.Service.InspectWorkspace(root), backend.Service.GetBinding(root, request.SessionId), backend.Service.LastDecision, backend.Clients?.Clients ?? []), EditorProtocolJsonContext.Default.EditorRemoteStatus);
            }
            case EditorProtocol.MethodDoctor:
            {
                var root = Require(message, EditorProtocolJsonContext.Default.RemoteEditorWorkspace, "ide.doctor requires workspaceRoot").WorkspaceRoot;
                root = backend.Paths?.Resolve(root) ?? root;
                return Serialize(await backend.Service.DoctorAsync(root, cancellationToken).ConfigureAwait(false), EditorProtocolJsonContext.Default.IReadOnlyListEditorDiagnostic);
            }
            default: throw MethodNotFound(name);
        }
    }

    private async ValueTask<EditorResult> OpenClientAsync(EditorRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _reverseRequests.Add(id, retired);
        }
        try
        {
            var result = await peer.RequestAsync(EditorProtocol.MethodClientOpen,
                Serialize(new RemoteEditorOpen(id, request), EditorProtocolJsonContext.Default.RemoteEditorOpen), cancellationToken).ConfigureAwait(false);
            return result is { } value
                ? JsonSerializer.Deserialize(value, EditorProtocolJsonContext.Default.EditorResult) ?? throw new IOException("IDE client returned an empty result.")
                : throw new IOException("IDE client returned no result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!lifetime.IsCancellationRequested && !peer.IsClosed)
            {
                try
                {
                    await peer.RequestAsync(EditorProtocol.MethodClientCancel,
                        Serialize(new RemoteEditorCancel(id), EditorProtocolJsonContext.Default.RemoteEditorCancel), lifetime).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
            }
            throw;
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return new EditorResult { Error = EditorErrorCode.CLIENT_UNAVAILABLE, Message = "The selected IDE client connection closed." };
        }
        finally
        {
            lock (_gate)
            {
                retired.TrySetResult();
                _reverseRequests.Remove(id);
            }
        }
    }
    private void Validate(EditorRequest request)
    {
        if (backend.Paths is not { } paths) return;
        var root = paths.Resolve(request.WorkspaceRoot);
        foreach (var path in new[] { request.Path, request.Left, request.Right })
            if (path is not null)
                paths.Resolve(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
    }


    private static async ValueTask<JsonElement?> ResultAsync(Task<EditorResult> operation)
        => Serialize(await operation.ConfigureAwait(false), EditorProtocolJsonContext.Default.EditorResult);
    private static async ValueTask<JsonElement?> AcknowledgeAsync(Task operation)
    {
        await operation.ConfigureAwait(false);
        return True;
    }
    private async Task RetireClientAsync()
    {
        IDisposable? registration;
        lock (_gate) { registration = _registration; _registration = null; }
        registration?.Dispose();
        Task[] pending;
        lock (_gate) pending = _reverseRequests.Values.Select(operation => operation.Task).ToArray();
        await Task.WhenAll(pending).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) _disposed = true;
        await RetireClientAsync().ConfigureAwait(false);
        await _operations.DisposeAsync().ConfigureAwait(false);
    }
}
