using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Sandbox;
using Dsh.Transport;

namespace Dsh.Plugins.Sandbox;

/** Broker checks are per plugin; plugins sharing this process are co-trusted. */
internal sealed class PluginSandboxConnection : IAsyncDisposable
{
    private readonly PluginSandboxSupervisor _owner;
    private readonly PluginSandboxSupervisor.Domain _domain;
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inbound = new(PluginSandboxMethods.MaximumPendingRequests);
    private readonly SemaphoreSlim _outbound = new(PluginSandboxMethods.MaximumPendingRequests);
    private readonly Dictionary<(string Package, int Tool), IDisposable> _tools = [];
    private readonly ConcurrentDictionary<string, Invocation> _invocations = new(StringComparer.Ordinal);
    private SandboxProcess? _process;
    private Task<SandboxProcess>? _launching;
    private JsonRpcPeer? _peer;
    private Task? _stderr;
    private Task? _cleanup;
    private int _aborted;
    private string? _startupDiagnostic;

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

    private PluginSandboxConnection(PluginSandboxSupervisor owner, PluginSandboxSupervisor.Domain domain)
        => (_owner, _domain) = (owner, domain);

    internal static async Task<PluginSandboxConnection> StartAsync(PluginSandboxSupervisor owner,
        PluginSandboxSupervisor.Domain domain, CancellationToken cancellationToken)
    {
        var connection = new PluginSandboxConnection(owner, domain);
        domain.Connection = connection;
        Task<SandboxProcess>? launching = null;
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection._lifetime.Token);
        var stage = "launch-options";
        try
        {
            startup.CancelAfter(PluginSandboxLaunch.StartupBudget);
            var code = domain.Runtime.Append(domain.Directory).ToArray();
            var options = PluginSandboxLaunch.Options(code);
            stage = "native-launch";
            launching = connection._launching = Task.Run(() => SandboxedProcess.StartAsync(options, connection._lifetime.Token), CancellationToken.None);
            connection._process = await launching.WaitAsync(startup.Token).ConfigureAwait(false);
            stage = "protocol-setup";
            var stream = new StandardIoDuplexStream(connection._process.StandardOutput,
                connection._process.StandardInput, ownsStreams: true);
            connection._peer = new JsonRpcPeer(stream, PluginSandboxMethods.MaximumFrameBytes)
            {
                RequestPreparation = connection.Prepare,
            };
            connection._peer.NotificationReceived += connection.Notification;
            connection._peer.Closed += _ => connection.Fault("The shared plugin worker connection closed.");
            connection._stderr = connection.DrainStderrAsync();
            connection._peer.Start();
            _ = connection.ObserveExitAsync();
            stage = "ready-request";
            var ready = await connection.RequestAsync(PluginSandboxMethods.Ready, new PluginSandboxAck(),
                PluginSandboxJsonContext.Default.PluginSandboxAck, startup.Token).ConfigureAwait(false);
            var value = ready?.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxReady);
            if (value is null || value.Version != PluginSandboxMethods.Version || value.Pid <= 0)
                throw new IOException("The isolated worker returned an incompatible handshake.");
            return connection;
        }
        catch (Exception error)
        {
            // Snapshot before Abort changes the lifetime token; include no paths or worker-supplied text.
            var cancellationDiagnostic = error is OperationCanceledException
                ? $"The isolated worker startup was cancelled (stage={stage}; launch-status={launching?.Status.ToString() ?? "not-scheduled"}; process-created={connection._process is not null}; startup-cancelled={startup.IsCancellationRequested}; caller-cancelled={cancellationToken.IsCancellationRequested}; lifetime-cancelled={connection._lifetime.IsCancellationRequested})."
                : null;
            connection.Abort();
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                if (error is OperationCanceledException cancellation)
                    throw new OperationCanceledException(cancellationDiagnostic, new AggregateException(error, cleanupError), cancellation.CancellationToken);
                throw new AggregateException("The isolated worker startup and cleanup failed.", error, cleanupError);
            }
            if (error is OperationCanceledException cancelled)
                throw new OperationCanceledException(cancellationDiagnostic, cancelled, cancelled.CancellationToken);
            throw new IOException($"The isolated worker could not start: {connection._startupDiagnostic ?? error.Message}", error);
        }
    }

    private ValueTask<JsonElement?>? Prepare(JsonRpcMessage message, CancellationToken cancellationToken)
        => new ValueTask<JsonElement?>(Task.Run(() => HandleAsync(message, cancellationToken), CancellationToken.None));

    private async Task<JsonElement?> HandleAsync(JsonRpcMessage message, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _inbound.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
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
                    Fault("The shared plugin worker sent an unsupported broker method.");
                    throw new IOException("The isolated worker requested an unsupported method.");
            }
        }
        finally { _inbound.Release(); }
    }

    private void Notification(JsonRpcMessage message)
    {
        if (message.Method != PluginSandboxMethods.Log)
        {
            Fault("The shared plugin worker sent an unsupported notification.");
            return;
        }
        try
        {
            if (message.Params is not { } parameters)
                throw new ArgumentException("The plugin log has no parameters.");
            Log(parameters.Deserialize(PluginSandboxJsonContext.Default.PluginSandboxLog)
                ?? throw new ArgumentException("The plugin log is invalid."));
        }
        catch (Exception error) { _owner.Diagnostic($"A plugin log was rejected: {error.Message}"); }
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
        lock (_sync)
        {
            if (Volatile.Read(ref _aborted) != 0)
                throw new IOException("The isolated worker is no longer active.");
            if (_tools.Count >= PluginSandboxMethods.MaximumTools
                || _tools.ContainsKey((request.Package, request.ToolId))
                || string.IsNullOrWhiteSpace(request.Name) || request.Parameters.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("The isolated worker tool registration is invalid or exceeds the tool limit.");
            var definition = new PluginToolDefinition(request.Name, request.Description, request.Parameters,
                (arguments, signal) => InvokeAsync(binding, request.ToolId, arguments, null, signal));
            var lease = ((IPluginExecutionHost)binding.Host).RegisterTool(definition,
                (arguments, execution, signal) => InvokeAsync(binding, request.ToolId, arguments, execution, signal));
            _tools.Add((request.Package, request.ToolId), lease);
        }
    }

    private void Unregister(PluginSandboxUnregisterTool request)
    {
        _ = Binding(request.Package);
        IDisposable? registration;
        lock (_sync) _tools.Remove((request.Package, request.ToolId), out registration);
        registration?.Dispose();
    }

    internal void ReleasePackage(string package)
    {
        IDisposable[] registrations;
        lock (_sync)
        {
            var keys = _tools.Keys.Where(key => key.Package == package).ToArray();
            registrations = keys.Select(key => _tools[key]).ToArray();
            foreach (var key in keys)
                _tools.Remove(key);
        }
        foreach (var registration in registrations)
            registration.Dispose();
    }

    private async ValueTask<JsonElement> InvokeAsync(PluginSandboxSupervisor.Binding binding, int tool,
        JsonElement arguments, ToolRunContext? execution, CancellationToken cancellationToken)
    {
        _owner.CheckAuthority(_domain, binding.Package);
        if (binding.Loading)
            throw new IOException("The isolated plugin has not completed initialization.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var token = Guid.NewGuid().ToString("N");
        using var invocation = new Invocation(binding, execution, linked);
        if (!_invocations.TryAdd(token, invocation))
            throw new IOException("The isolated invocation token could not be allocated.");
        try
        {
            var result = await RequestAsync(PluginSandboxMethods.Invoke,
                new PluginSandboxInvokeRequest(binding.Package, tool, arguments, token),
                PluginSandboxJsonContext.Default.PluginSandboxInvokeRequest, linked.Token).ConfigureAwait(false);
            _owner.CheckAuthority(_domain, binding.Package);
            return result ?? throw new IOException("The isolated tool returned no result.");
        }
        finally { _invocations.TryRemove(token, out _); }
    }

    private async Task<JsonElement> HostCallAsync(PluginSandboxHostCall request, CancellationToken cancellationToken)
    {
        var binding = Binding(request.Package);
        if (request.Invocation is not { } token)
            return await ((IPluginExecutionHost)binding.Host).CallAsync(request.Operation, request.Arguments,
                null, cancellationToken).ConfigureAwait(false);
        if (!_invocations.TryGetValue(token, out var invocation) || !ReferenceEquals(invocation.Binding, binding))
            throw new UnauthorizedAccessException("The invocation token is stale or belongs to another package.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, invocation.Cancellation.Token);
        return await ((IPluginExecutionHost)binding.Host).CallAsync(request.Operation, request.Arguments,
            invocation.Execution, linked.Token).ConfigureAwait(false);
    }

    private void Log(PluginSandboxLog message)
        => Binding(message.Package).Host.Log(message.Level, message.Message);

    internal async Task<JsonElement?> RequestAsync<T>(string method, T value, JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _outbound.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _aborted) != 0)
                throw new IOException("The isolated worker is no longer available.");
            var parameters = JsonSerializer.SerializeToElement(value, type);
            using var cancellation = cancellationToken.Register(() => Fault("The shared plugin worker operation was cancelled or timed out."));
            return await (_peer ?? throw new IOException("The isolated protocol is unavailable."))
                .RequestAsync(method, parameters, linked.Token).ConfigureAwait(false);
        }
        finally { _outbound.Release(); }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[1024];
        try
        {
            using var reader = new StreamReader(_process!.StandardError, Encoding.UTF8, leaveOpen: true);
            while (true)
            {
                var count = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (count == 0)
                    return;
                var text = new string(buffer, 0, count);
                _startupDiagnostic = text;
                _owner.Diagnostic(text);
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { Fault("The shared plugin worker stderr stream failed."); }
    }

    private async Task ObserveExitAsync()
    {
        try
        {
            if (_process is null)
                return;
            await _process.WaitForExitAsync().ConfigureAwait(false);
            Fault("The shared plugin worker exited without replaying any invocation.");
        }
        catch (Exception) { Fault("The shared plugin worker lifetime failed."); }
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

    internal Task ShutdownAsync()
    {
        Abort();
        lock (_sync) return _cleanup!;
    }

    public ValueTask DisposeAsync()
        => new(ShutdownAsync().WaitAsync(PluginSandboxLaunch.ShutdownBudget));

    private async Task CleanupAsync()
    {
        IDisposable[] registrations;
        lock (_sync)
        {
            registrations = _tools.Values.ToArray();
            _tools.Clear();
        }
        foreach (var registration in registrations)
            registration.Dispose();
        var peerCleanup = _peer?.DisposeAsync().AsTask() ?? Task.CompletedTask;
        var processCleanup = ReleaseProcessAsync();
        await Task.WhenAll(peerCleanup, processCleanup, _stderr ?? Task.CompletedTask).ConfigureAwait(false);
    }

    private async Task ReleaseProcessAsync()
    {
        var process = _process;
        if (process is null && _launching is not null)
        {
            try { process = await _launching.ConfigureAwait(false); }
            catch (Exception) { return; }
        }
        if (process is not null)
            await process.DisposeAsync().ConfigureAwait(false);
    }
}
