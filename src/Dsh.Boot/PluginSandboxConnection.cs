using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Boot;

/** A connection is the authority boundary; wire-supplied package names and tokens never create authority. */
internal sealed class PluginSandboxConnection : IAsyncDisposable
{
    private const int MaximumPendingCalls = 16;
    private const int MaximumLogsPerSecond = 32;
    private const int MaximumRegistrationBytes = 512 * 1024;
    private const int MaximumSchemaBytes = 32 * 1024;
    private const int MaximumStderrBytes = 8192;
    private readonly PluginSandboxSupervisor _owner;
    private readonly PluginSandboxSupervisor.Domain _domain;
    private readonly PluginCapabilityFactory _capabilities;
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _admitted = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Package, int Tool), Registration> _tools = [];
    private readonly ConcurrentDictionary<string, Invocation> _invocations = new(StringComparer.Ordinal);
    private SandboxProcess? _process;
    private JsonRpcPeer? _peer;
    private Task? _stderr;
    private Task? _cleanup;
    private int _pendingCalls;
    private int _registrationBytes;
    private int _aborted;
    private long _logWindow = Environment.TickCount64;
    private int _logs;
    private readonly byte[] _stderrCapture = new byte[MaximumStderrBytes];
    private int _stderrBytes;
    private bool _ready;
    private string? _startupDiagnostic;

    private sealed record Registration(IDisposable Lease, int Bytes);
    private sealed class Invocation(PluginSandboxSupervisor.Binding binding, ToolRunContext? execution,
        CancellationTokenSource cancellation) : IDisposable
    {
        internal PluginSandboxSupervisor.Binding Binding { get; } = binding;
        internal ToolRunContext? Execution { get; } = execution;
        internal CancellationTokenSource Cancellation { get; } = cancellation;
        public void Dispose()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
        }
    }

    private PluginSandboxConnection(PluginSandboxSupervisor owner, PluginSandboxSupervisor.Domain domain,
        PluginCapabilityFactory capabilities)
        => (_owner, _domain, _capabilities) = (owner, domain, capabilities);

    internal static async Task<PluginSandboxConnection> StartAsync(PluginSandboxSupervisor owner,
        PluginSandboxSupervisor.Domain domain, PluginCapabilityFactory capabilities, CancellationToken cancellationToken)
    {
        var connection = new PluginSandboxConnection(owner, domain, capabilities);
        Task<SandboxProcess>? launching = null;
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection._lifetime.Token);
            startup.CancelAfter(PluginSandboxLaunch.StartupBudget);
            var options = PluginSandboxLaunch.Options(domain.Code.ToArray());
            launching = Task.Run(() => SandboxedProcess.StartAsync(options, connection._lifetime.Token), CancellationToken.None);
            connection._process = await launching.WaitAsync(startup.Token).ConfigureAwait(false);
            var stream = new AdmissionStream(new StandardIoDuplexStream(connection._process.StandardOutput,
                connection._process.StandardInput, ownsStreams: true), connection);
            connection._peer = new JsonRpcPeer(stream, PluginSandboxMethods.MaximumFrameBytes)
            {
                RequestPreparation = connection.Prepare,
            };
            connection._peer.NotificationReceived += connection.Notification;
            connection._peer.Closed += _ => connection.Fault("The isolated worker connection closed; its trust domain was terminated.");
            connection._stderr = connection.DrainStderrAsync();
            connection._peer.Start();
            _ = connection.ObserveExitAsync();
            var ready = await connection.RequestAsync(PluginSandboxMethods.Ready, new PluginSandboxAck(),
                PluginSandboxJsonContext.Default.PluginSandboxAck, startup.Token).ConfigureAwait(false);
            var value = ready?.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxReady);
            if (value is null || value.Version != PluginSandboxMethods.Version || value.Pid <= 0)
                throw new IOException("The isolated worker returned an incompatible handshake.");
            connection._ready = true;
            return connection;
        }
        catch (Exception error)
        {
            connection.Abort();
            if (connection._process is null && launching is not null)
                _ = ReapLateLaunchAsync(launching);
            await connection.DisposeAsync().ConfigureAwait(false);
            if (error is OperationCanceledException)
                throw;
            throw new IOException($"The isolated worker could not start: {connection._startupDiagnostic ?? error.Message}", error);
        }
    }

    private static async Task ReapLateLaunchAsync(Task<SandboxProcess> launching)
    {
        try { await (await launching.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* A failed native launch has no returned process to own. */ }
    }

    private ValueTask<JsonElement?>? Prepare(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (Volatile.Read(ref _aborted) != 0 || message.IdKey is not { } id
                || _admitted.Count >= PluginSandboxMethods.MaximumInboundRequests || !_admitted.Add(id))
            {
                Fault("The isolated worker exceeded bounded receive admission; its trust domain was terminated.");
                throw new IOException("Isolated worker receive admission failed.");
            }
        }
        // Admission remains reserved until the corresponding response frame is actually written.
        return new ValueTask<JsonElement?>(Task.Run(() => HandleAsync(message, cancellationToken), CancellationToken.None));
    }

    private async Task<JsonElement?> HandleAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var parameters = message.Params ?? throw new ArgumentException("The sandbox request has no parameters.");
        switch (message.Method)
        {
            case PluginSandboxMethods.RegisterTool:
                Register(parameters.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxToolRequest)
                    ?? throw new ArgumentException("The tool registration is invalid."));
                return Ack();
            case PluginSandboxMethods.UnregisterTool:
                Unregister(parameters.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxUnregisterTool)
                    ?? throw new ArgumentException("The tool unregistration is invalid."));
                return Ack();
            case PluginSandboxMethods.HostCall:
                return await HostCallAsync(parameters.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxHostCall)
                    ?? throw new ArgumentException("The host call is invalid."), linked.Token).ConfigureAwait(false);
            case PluginSandboxMethods.Log:
                Log(parameters.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxLog)
                    ?? throw new ArgumentException("The plugin log is invalid."));
                return Ack();
            default:
                Fault("The isolated worker sent an unsupported broker method; its trust domain was terminated.");
                throw new IOException("The isolated worker requested an unsupported method.");
        }
    }

    private void Notification(JsonRpcMessage message)
    {
        try
        {
            if (message.Method != PluginSandboxMethods.Log || message.Params is not { } parameters)
                throw new IOException("The isolated worker sent an unsupported notification.");
            Log(parameters.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxLog)
                ?? throw new IOException("The isolated worker sent an invalid log."));
        }
        catch (Exception)
        {
            Fault("The isolated worker sent an invalid or excessive notification; its trust domain was terminated.");
        }
    }

    private PluginSandboxSupervisor.Binding Binding(string package)
    {
        _owner.CheckAuthority(_domain, package);
        return _domain.Bindings.GetValueOrDefault(package)
            ?? throw new UnauthorizedAccessException("The package is not bound to this worker.");
    }

    private void Register(PluginSandboxToolRequest request)
    {
        var binding = Binding(request.Package);
        var bytes = Encoding.UTF8.GetByteCount(request.Name) + Encoding.UTF8.GetByteCount(request.Description)
            + Encoding.UTF8.GetByteCount(request.Parameters.GetRawText());
        lock (_sync)
        {
            if (Volatile.Read(ref _aborted) != 0)
                throw new IOException("The isolated worker is no longer active.");
            if (_tools.Count >= PluginCapabilityFactory.MaximumRegistrations
                || bytes > MaximumRegistrationBytes - _registrationBytes
                || Encoding.UTF8.GetByteCount(request.Parameters.GetRawText()) > MaximumSchemaBytes
                || _tools.ContainsKey((request.Package, request.ToolId))
                || string.IsNullOrWhiteSpace(request.Name) || request.Parameters.ValueKind != JsonValueKind.Object)
            {
                Fault("The isolated worker exceeded its tool registration boundary; its trust domain was terminated.");
                throw new IOException("The isolated worker tool registration limit was exceeded.");
            }
            var lease = binding.Host.RegisterTool(new PluginToolDefinition(request.Name, request.Description,
                request.Parameters, (arguments, signal) => InvokeAsync(binding, request.ToolId, arguments, signal)));
            _tools.Add((request.Package, request.ToolId), new Registration(lease, bytes));
            _registrationBytes += bytes;
        }
    }

    private void Unregister(PluginSandboxUnregisterTool request)
    {
        _ = Binding(request.Package);
        Registration? registration;
        lock (_sync)
        {
            _tools.Remove((request.Package, request.ToolId), out registration);
            if (registration is not null)
                _registrationBytes -= registration.Bytes;
        }
        registration?.Lease.Dispose();
    }

    private async ValueTask<JsonElement> InvokeAsync(PluginSandboxSupervisor.Binding binding, int tool,
        JsonElement arguments, CancellationToken cancellationToken)
    {
        _owner.CheckAuthority(_domain, binding.Package);
        if (binding.Loading)
            throw new IOException("The isolated plugin has not completed initialization.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = Guid.NewGuid().ToString("N");
        using var invocation = new Invocation(binding, SettingsMutationScope.Current?.Context, linked);
        if (!_invocations.TryAdd(token, invocation))
            throw new IOException("The isolated invocation token could not be allocated.");
        try
        {
            // Cancellation revokes the entire owned OS domain, including retained native/managed threads.
            using var cancellation = cancellationToken.Register(() => Fault("The isolated trust domain was terminated by user cancellation."));
            var result = await RequestAsync(PluginSandboxMethods.Invoke,
                new PluginSandboxInvokeRequest(binding.Package, tool, arguments, token),
                PluginSandboxJsonContext.Default.PluginSandboxInvokeRequest, linked.Token).ConfigureAwait(false);
            _owner.CheckAuthority(_domain, binding.Package);
            return result ?? throw new IOException("The isolated tool returned no result.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Fault("The isolated trust domain was terminated by user cancellation.");
            throw;
        }
        finally { _invocations.TryRemove(token, out _); }
    }

    private async Task<JsonElement> HostCallAsync(PluginSandboxHostCall request, CancellationToken cancellationToken)
    {
        var binding = Binding(request.Package);
        if (request.Invocation is not { } token)
            return await _capabilities.ExecuteAsync(binding.Package, request.Operation, request.Arguments,
                execution: null, _domain.Key.Permissions, cancellationToken).ConfigureAwait(false);
        if (!_invocations.TryGetValue(token, out var invocation) || !ReferenceEquals(invocation.Binding, binding))
            throw new UnauthorizedAccessException("The invocation token is stale, forged or belongs to another package.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, invocation.Cancellation.Token);
        return await _capabilities.ExecuteAsync(binding.Package, request.Operation, request.Arguments,
            invocation.Execution, _domain.Key.Permissions, linked.Token).ConfigureAwait(false);
    }

    private void Log(PluginSandboxLog message)
    {
        var binding = Binding(message.Package);
        lock (_sync)
        {
            var now = Environment.TickCount64;
            if (now - _logWindow >= 1000)
            {
                _logWindow = now;
                _logs = 0;
            }
            if (message.Message.Length > PluginCapabilityFactory.MaximumLogCharacters || ++_logs > MaximumLogsPerSecond)
            {
                Fault("The isolated worker exceeded its bounded log rate or length; its trust domain was terminated.");
                throw new IOException("The isolated worker log limit was exceeded.");
            }
        }
        binding.Host.Log(message.Level, message.Message);
    }

    internal async Task<JsonElement?> RequestAsync<T>(string method, T value, JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _pendingCalls) > MaximumPendingCalls)
        {
            Interlocked.Decrement(ref _pendingCalls);
            Fault("The isolated worker exceeded bounded outbound invocation capacity; its trust domain was terminated.");
            throw new IOException("The isolated worker outbound invocation capacity is exhausted.");
        }
        try
        {
            if (Volatile.Read(ref _aborted) != 0)
                throw new IOException("The isolated worker is no longer available.");
            var parameters = JsonSerializer.SerializeToElement(value, type);
            if (Encoding.UTF8.GetByteCount(parameters.GetRawText()) > 512 * 1024)
                throw new IOException("The isolated request exceeds the bounded protocol frame size.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            using var cancellation = cancellationToken.Register(() => Fault("The isolated worker operation was cancelled or exceeded its security lifecycle budget."));
            return await (_peer ?? throw new IOException("The isolated protocol is unavailable."))
                .RequestAsync(method, parameters, linked.Token).ConfigureAwait(false);
        }
        finally { Interlocked.Decrement(ref _pendingCalls); }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new byte[1024];
        try
        {
            while (_process is not null)
            {
                var count = await _process.StandardError.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (count == 0)
                    return;
                if (count > MaximumStderrBytes - _stderrBytes)
                {
                    Fault("The isolated worker exceeded bounded stderr capture; its trust domain was terminated.");
                    return;
                }
                buffer.AsSpan(0, count).CopyTo(_stderrCapture.AsSpan(_stderrBytes));
                _stderrBytes += count;
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Fault("The isolated worker stderr stream failed; its trust domain was terminated."); }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            if (_process is null)
                return;
            await _process.WaitForExitAsync().ConfigureAwait(false);
            Fault(_process.WasMemoryLimitExceeded
                ? "The isolated worker exceeded its enforced or measured platform memory budget; its trust domain was terminated."
                : "The isolated worker exited; its trust domain was terminated without replaying any invocation.");
        }
        catch (Exception) { Fault("The isolated worker lifetime failed; its trust domain was terminated."); }
    }

    private static JsonElement Ack() => JsonSerializer.SerializeToElement(new PluginSandboxAck(), PluginSandboxJsonContext.Default.PluginSandboxAck);

    private void Fault(string reason)
    {
        Abort();
        _owner.Fault(_domain, reason);
    }

    internal void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) == 0)
        {
            _lifetime.Cancel();
            _process?.Kill();
        }
        lock (_sync) _cleanup ??= Task.Run(CleanupAsync);
    }

    public ValueTask DisposeAsync()
    {
        Abort();
        lock (_sync) return new ValueTask(_cleanup!);
    }

    private async Task CleanupAsync()
    {
        Registration[] registrations;
        lock (_sync)
        {
            registrations = _tools.Values.ToArray();
            _tools.Clear();
            _registrationBytes = 0;
        }
        foreach (var registration in registrations)
            registration.Lease.Dispose();
        var peerCleanup = _peer?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        var processCleanup = _process?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        await Task.WhenAll(peerCleanup, processCleanup, _stderr ?? Task.CompletedTask)
            .WaitAsync(PluginSandboxLaunch.ShutdownBudget).ConfigureAwait(false);
        if (!_ready && _stderrBytes != 0)
            _startupDiagnostic = Encoding.UTF8.GetString(_stderrCapture.AsSpan(0, _stderrBytes));
        Array.Clear(_stderrCapture);
    }

    /** FrameCodec writes each complete frame once. Release receive admission only after its response leaves stdio. */
    private sealed class AdmissionStream(Stream inner, PluginSandboxConnection owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Release(buffer.AsSpan(offset, count));
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            Release(buffer.Span);
        }
        private void Release(ReadOnlySpan<byte> frame)
        {
            var reader = new Utf8JsonReader(frame[FrameCodec.HeaderLength..]);
            string? id = null;
            var response = false;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    continue;
                if (reader.ValueTextEquals("id"))
                {
                    reader.Read();
                    id = reader.TokenType == JsonTokenType.String ? reader.GetString()
                        : reader.TokenType == JsonTokenType.Number ? Encoding.UTF8.GetString(reader.ValueSpan) : null;
                }
                else if (reader.ValueTextEquals("result") || reader.ValueTextEquals("error"))
                {
                    response = true;
                    reader.Read();
                    reader.Skip();
                }
                else
                {
                    reader.Read();
                    reader.Skip();
                }
            }
            if (response && id is not null)
                lock (owner._sync) owner._admitted.Remove(id);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
