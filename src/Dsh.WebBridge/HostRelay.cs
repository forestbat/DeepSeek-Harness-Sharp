using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Dsh.Fleet;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.WebBridge;

/** A browser's protocol adapter, not a second host client: snapshots come from Fleet, streams use its public resumable APIs, and user commands use the current RemoteHost lease. No replay, sequence tracking, pending cache or machine reconnect loop lives here. */
internal sealed class HostRelay(WebSocket socket, MachineConnection machine, Func<string, string> redact, CancellationToken cancellationToken)
{
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly CancellationTokenSource _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly Channel<byte> _changed = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly Dictionary<string, CancellationTokenSource> _subscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TerminalOutput> _terminals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Task, byte> _operations = new();
    // HostUploads belongs to the shared Host connection, but unfinished uploads still belong to
    // the browser that created them. Keep the original lease; reconnecting must never retarget them.
    private readonly ConcurrentDictionary<string, HostLease> _uploads = new(StringComparer.Ordinal);

    public static async Task RunAsync(WebSocket socket, MachineConnection machine, Func<string, string> redact, CancellationToken cancellationToken)
    {
        var relay = new HostRelay(socket, machine, redact, cancellationToken);
        await relay.RunAsync().ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        machine.StateChanged += Changed;
        machine.SessionsChanged += Changed;
        machine.PendingChanged += Changed;
        machine.NotificationsChanged += NotificationsChanged;
        Changed(machine);
        var view = PublishViewsAsync();
        try
        {
            var chunk = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (!_lifetime.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(chunk.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    break;
                if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > FrameCodec.DefaultMaxFrameBytes)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidMessageType, "expected a JSON-RPC text message within the host frame limit", _lifetime.Token).ConfigureAwait(false);
                    break;
                }
                message.Write(chunk, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;
                var request = JsonSerializer.Deserialize(message.GetBuffer().AsSpan(0, (int)message.Length), JsonRpcJsonContext.Default.JsonRpcMessage);
                message.SetLength(0);
                if (request?.IsRequest == true)
                    Track(DispatchAsync(request));
            }
        }
        catch (Exception error) when (IsConnectionEnd(error) || error is JsonException) { }
        finally
        {
            machine.StateChanged -= Changed;
            machine.SessionsChanged -= Changed;
            machine.PendingChanged -= Changed;
            machine.NotificationsChanged -= NotificationsChanged;
            _connectionLifetime.CancelAfter(GracefulCloseTimeout);
            await _lifetime.CancelAsync().ConfigureAwait(false);
            try { await view.ConfigureAwait(false); }
            catch (Exception error) when (IsConnectionEnd(error)) { }
            await Task.WhenAll(_operations.Keys).ConfigureAwait(false);
            foreach (var (uploadId, lease) in _uploads)
                await AbortUploadAsync(uploadId, lease).ConfigureAwait(false);
            _uploads.Clear();
            foreach (var lifetime in _subscriptions.Values)
                lifetime.Dispose();
            foreach (var terminal in _terminals.Values)
                terminal.Dispose();
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", _connectionLifetime.Token).ConfigureAwait(false); }
                catch (Exception error) when (IsConnectionEnd(error)) { }
            }
            _writes.Dispose();
            _connectionLifetime.Dispose();
            _lifetime.Dispose();
        }
    }

    private void Track(Task operation)
    {
        _operations.TryAdd(operation, 0);
        _ = operation.ContinueWith(task => _operations.TryRemove(task, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private void Changed(IWorkspaceMachine _) => _changed.Writer.TryWrite(0);
    private void NotificationsChanged(IWorkspaceMachine connection, RemoteNotificationChange? _) => Changed(connection);

    private async Task PublishViewsAsync()
    {
        await foreach (var _ in _changed.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
        {
            var view = new WebMachineView(machine.State.ToString(), machine.Detail is { } detail ? redact(detail) : null,
                machine.Hello, machine.Info, machine.Sessions, machine.Pending, machine.Notifications);
            await NotifyAsync(BridgeProtocol.NotificationView, view, WebBridgeJsonContext.Default.WebMachineView, _lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(JsonRpcMessage request)
    {
        try
        {
            var result = await HandleAsync(request).ConfigureAwait(false);
            await SendAsync(new JsonRpcMessage { Id = request.Id, Result = result }, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or WebSocketException or InvalidOperationException or NotSupportedException or ArgumentException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            if (_lifetime.IsCancellationRequested)
                return;
            try
            {
                await SendAsync(new JsonRpcMessage { Id = request.Id, Error = new JsonRpcError { Code = error is JsonRpcException rpc ? rpc.Code : -32603, Message = redact(error.Message) } }, _lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception ended) when (IsConnectionEnd(ended)) { }
        }
    }

    private async Task<JsonElement> HandleAsync(JsonRpcMessage request)
    {
        var parameters = request.Params;
        switch (request.Method)
        {
            case HostProtocol.MethodSessionSubscribe:
            {
                var sessionId = Required(parameters, "SessionId");
                var viewId = Required(parameters, "ViewId");
                StopSubscription(viewId);
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _subscriptions[viewId] = lifetime;
                var from = parameters!.Value.TryGetProperty("FromSeq", out var sequence) ? sequence.GetInt64() : 0;
                Track(FollowSessionAsync(sessionId, viewId, from, lifetime.Token));
                return True;
            }
            case HostProtocol.MethodSessionUnsubscribe:
                StopSubscription(Required(parameters, "ViewId"));
                return True;
            case PtyProtocol.MethodPtyAttach:
            {
                var ptyId = Required(parameters, "PtyId");
                var viewId = Required(parameters, "ViewId");
                StopTerminal(viewId);
                var terminal = new TerminalOutput(this, ptyId, viewId, _lifetime.Token);
                if (parameters!.Value.TryGetProperty("Columns", out var columns) && parameters.Value.TryGetProperty("Rows", out var rows))
                    terminal.Size = (columns.GetInt32(), rows.GetInt32());
                _terminals[viewId] = terminal;
                Track(FollowTerminalAsync(terminal));
                return True;
            }
            case PtyProtocol.MethodPtyDetach:
                StopTerminal(Required(parameters, "ViewId"));
                return True;
            case PtyProtocol.MethodPtyResize:
            {
                var ptyId = Required(parameters, "PtyId");
                var size = (Columns: parameters!.Value.GetProperty("Columns").GetInt32(), Rows: parameters.Value.GetProperty("Rows").GetInt32());
                if (_terminals.TryGetValue(Required(parameters, "ViewId"), out var terminal))
                    terminal.Size = size;
                // Geometry is retained for prepare while offline; user input and other operations are never queued for reconnection.
                if (machine.State == MachineConnectionState.Connected)
                    await ConnectedLease().Client.Pty.ResizePtyAsync(ptyId, size.Columns, size.Rows, _lifetime.Token).ConfigureAwait(false);
                return True;
            }
        }
        if (request.Method == FilesProtocol.MethodFsUploadBegin)
            return await BeginUploadAsync(parameters).ConfigureAwait(false);
        if (request.Method is FilesProtocol.MethodFsUploadChunk or FilesProtocol.MethodFsUploadCommit or FilesProtocol.MethodFsUploadAbort)
            return await ForwardUploadAsync(request.Method, parameters).ConfigureAwait(false);
        var lease = ConnectedLease();
        if (request.Method == HostProtocol.MethodHello)
            return JsonSerializer.SerializeToElement(lease.Client.Hello, HostProtocolJsonContext.Default.HostHelloResponse);
        return await lease.Client.RequestAsync(request.Method!, parameters, _lifetime.Token).ConfigureAwait(false);
    }

    private async Task<JsonElement> BeginUploadAsync(JsonElement? parameters)
    {
        var lease = ConnectedLease();
        // Once sent, begin must reach its response even if this browser closes. Otherwise the
        // newly created UploadId and its FileShare.None temporary handle would be orphaned.
        var result = await lease.Client.RequestAsync(FilesProtocol.MethodFsUploadBegin, parameters, lease.Lifetime).ConfigureAwait(false);
        var uploadId = Required(result, "UploadId");
        _uploads[uploadId] = lease;
        if (_lifetime.IsCancellationRequested && _uploads.TryRemove(uploadId, out _))
            await AbortUploadAsync(uploadId, lease).ConfigureAwait(false);
        return result;
    }

    private async Task<JsonElement> ForwardUploadAsync(string method, JsonElement? parameters)
    {
        var uploadId = Required(parameters, "UploadId");
        if (!_uploads.TryGetValue(uploadId, out var lease))
            throw new JsonRpcException(-32602, "unknown upload for this browser");
        // Join an operation already sent before disconnect cleanup aborts its temporary file.
        JsonElement result;
        try { result = await lease.Client.RequestAsync(method, parameters, lease.Lifetime).ConfigureAwait(false); }
        catch
        {
            if (method is FilesProtocol.MethodFsUploadCommit or FilesProtocol.MethodFsUploadAbort
                && _uploads.TryRemove(uploadId, out _))
                await AbortUploadAsync(uploadId, lease).ConfigureAwait(false);
            throw;
        }
        if (method is FilesProtocol.MethodFsUploadCommit or FilesProtocol.MethodFsUploadAbort)
            _uploads.TryRemove(uploadId, out _);
        return result;
    }

    private static async Task AbortUploadAsync(string uploadId, HostLease lease)
    {
        try { await lease.Client.Files.AbortUploadAsync(uploadId, lease.Lifetime).ConfigureAwait(false); }
        catch (Exception error) when (IsConnectionEnd(error) || error is JsonRpcException) { }
    }

    private HostLease ConnectedLease()
    {
        var connected = machine.WaitConnectedAsync(_lifetime.Token);
        if (!connected.IsCompletedSuccessfully)
            throw new IOException($"{machine.Machine.Name} is not connected");
        return connected.GetAwaiter().GetResult();
    }

    private static readonly JsonElement True = JsonSerializer.SerializeToElement(true, HostProtocolJsonContext.Default.Boolean);
    private static string Required(JsonElement? parameters, string name)
        => parameters is { } value && value.TryGetProperty(name, out var item) && item.GetString() is { Length: > 0 } text ? text : throw new JsonRpcException(-32602, $"missing {name}");

    private void StopSubscription(string viewId)
    {
        if (_subscriptions.Remove(viewId, out var lifetime))
        {
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }

    private void StopTerminal(string viewId)
    {
        if (_terminals.Remove(viewId, out var terminal))
            terminal.Dispose();
    }

    private async Task FollowSessionAsync(string sessionId, string viewId, long from, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in machine.SubscribeAsync(sessionId, from, cancellationToken).ConfigureAwait(false))
                await NotifyAsync(HostProtocol.NotificationEvent, new WebSessionEvent(item.SessionId, item.Event is { } sessionEvent ? DshJson.Serialize(sessionEvent) : "", item.Reset, viewId), WebBridgeJsonContext.Default.WebSessionEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (IsConnectionEnd(error) || error is JsonRpcException)
        {
            if (!cancellationToken.IsCancellationRequested)
                await StreamErrorAsync(BridgeProtocol.SessionStream, viewId, error).ConfigureAwait(false);
        }
    }

    private async Task FollowTerminalAsync(TerminalOutput terminal)
    {
        try
        {
            using var input = new IdleInput();
            await machine.AttachPtyAsync(terminal.PtyId, input, terminal, terminal.Token, async (client, token) =>
            {
                var size = terminal.Size;
                if (size is { } geometry)
                    await client.Pty.ResizePtyAsync(terminal.PtyId, geometry.Columns, geometry.Rows, token).ConfigureAwait(false);
                terminal.SnapshotNext = true;
            }).ConfigureAwait(false);
            await NotifyAsync(PtyProtocol.NotificationPtyOutput, new WebTerminalOutput(terminal.PtyId, "", true, false, terminal.ViewId), WebBridgeJsonContext.Default.WebTerminalOutput, terminal.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (IsConnectionEnd(error) || error is JsonRpcException)
        {
            if (!terminal.Token.IsCancellationRequested)
                await StreamErrorAsync(BridgeProtocol.TerminalStream, terminal.ViewId, error).ConfigureAwait(false);
        }
        finally
        {
            terminal.Dispose();
            terminal.Release();
        }
    }

    private async Task StreamErrorAsync(string kind, string id, Exception error)
    {
        try { await NotifyAsync(BridgeProtocol.NotificationStreamError, new WebStreamError(kind, id, redact(error.Message)), WebBridgeJsonContext.Default.WebStreamError, _lifetime.Token).ConfigureAwait(false); }
        catch (Exception ended) when (IsConnectionEnd(ended)) { }
    }

    private Task NotifyAsync<T>(string method, T parameters, JsonTypeInfo<T> info, CancellationToken cancellationToken)
        => SendAsync(new JsonRpcMessage { Method = method, Params = JsonSerializer.SerializeToElement(parameters, info) }, cancellationToken);

    private async Task SendAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, JsonRpcJsonContext.Default.JsonRpcMessage);
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Cancelling a ManagedWebSocket send aborts the entire socket, not just this view.
            // A detached view may stop waiting for the writer, but cannot cancel an in-flight frame.
            await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, _connectionLifetime.Token).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    private static bool IsConnectionEnd(Exception error) => error is IOException or WebSocketException or OperationCanceledException or ObjectDisposedException;

    private sealed class TerminalOutput(HostRelay owner, string ptyId, string viewId, CancellationToken cancellationToken) : Stream
    {
        private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        private readonly Lock _geometry = new();
        private (int Columns, int Rows)? _size;
        private int _stopped;
        public string PtyId => ptyId;
        public string ViewId => viewId;
        public CancellationToken Token => _stop.Token;
        public (int Columns, int Rows)? Size { get { lock (_geometry) return _size; } set { lock (_geometry) _size = value; } }
        public bool SnapshotNext { get; set; } = true;
        public void Release() => _stop.Dispose();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var snapshot = SnapshotNext;
            SnapshotNext = false;
            await owner.NotifyAsync(PtyProtocol.NotificationPtyOutput, new WebTerminalOutput(ptyId, Convert.ToBase64String(buffer.Span), false, snapshot, viewId), WebBridgeJsonContext.Default.WebTerminalOutput, cancellationToken).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing) { if (disposing && Interlocked.Exchange(ref _stopped, 1) == 0) _stop.Cancel(); base.Dispose(disposing); }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class IdleInput : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false); return 0; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

internal sealed record WebMachineView(string State, string? Detail, HostHelloResponse? Hello, HostInfo? Info, IReadOnlyList<RemoteSessionSummary> Sessions, RemotePendingList Pending, RemoteNotifyPage? Notifications);
internal sealed record WebTerminalOutput(string PtyId, string Base64, bool Eof, bool Snapshot, string ViewId);
internal sealed record WebSessionEvent(string SessionId, string EventJson, bool Reset, string ViewId);
internal sealed record WebStreamError(string Kind, string ViewId, string Message);
