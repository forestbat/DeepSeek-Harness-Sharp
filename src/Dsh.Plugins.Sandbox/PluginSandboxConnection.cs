using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Dsh.Core;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Sandbox;
using Dsh.Transport;
using Dsh.Agent;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;

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
    private OwnedProcess? _process;
    private Task<OwnedProcess>? _launching;
    private JsonRpcChannel? _peer;
    private Task? _stderr;
    private Task? _cleanup;
    private int _aborted;
    private string? _startupDiagnostic;
    private const string StartupCancelledMessage = "The sandbox worker startup was cancelled.";

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
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection._lifetime.Token);
        try
        {
            startup.CancelAfter(PluginSandboxLaunch.StartupBudget);
            var code = domain.Runtime.Append(domain.Directory).ToArray();
            var options = PluginSandboxLaunch.Options(code);
            var launching = connection._launching = Task.Run(() => SandboxedProcess.StartAsync(options, connection._lifetime.Token), CancellationToken.None);
            connection._process = await launching.WaitAsync(startup.Token).ConfigureAwait(false);
            var stream = new StandardIoDuplexStream(connection._process.StandardOutput,
                connection._process.StandardInput, ownsStreams: true);
            var channel = JsonRpcChannel.ServeMethods(stream, PluginSandboxWire.Formatter(), connection.Methods());
            connection._peer = channel;
            _ = connection.ObserveClosedAsync(channel);
            connection._stderr = connection.DrainStderrAsync();
            _ = connection.ObserveExitAsync();
            var ready = await channel.InvokeAsync<PluginSandboxReady>(PluginSandboxMethods.Ready, startup.Token, new PluginSandboxAck()).ConfigureAwait(false);
            if (ready is null || ready.Version != PluginSandboxMethods.Version || ready.Pid <= 0)
                throw new IOException("The isolated worker returned an incompatible handshake.");
            return connection;
        }
        catch (Exception error)
        {
            connection.Abort();
            try { await connection.DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                if (error is OperationCanceledException cancellation)
                    throw new OperationCanceledException(StartupCancelledMessage, new AggregateException(error, cleanupError), cancellation.CancellationToken);
                throw new AggregateException("The isolated worker startup and cleanup failed.", error, cleanupError);
            }
            if (error is OperationCanceledException cancelled)
                throw new OperationCanceledException(StartupCancelledMessage, cancelled, cancelled.CancellationToken);
            throw new IOException($"The isolated worker could not start: {connection._startupDiagnostic ?? error.Message}", error);
        }
    }

    private async Task ObserveClosedAsync(JsonRpcChannel channel)
    {
        try
        {
            await channel.Completion.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 断连本身由 Fault 统一收口, 这里只负责让 Fault 被触发一次。
        }
        Fault("The shared plugin worker connection closed.");
    }

    public async Task<PluginSandboxAck> RegisterToolAsync(PluginSandboxToolRequest request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _inbound.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Register(request);
            return new PluginSandboxAck();
        }
        catch (Exception error)
        {
            throw Failed(error);
        }
        finally { _inbound.Release(); }
    }

    public async Task<PluginSandboxAck> UnregisterToolAsync(PluginSandboxUnregisterTool request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _inbound.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Unregister(request);
            return new PluginSandboxAck();
        }
        catch (Exception error)
        {
            throw Failed(error);
        }
        finally { _inbound.Release(); }
    }

    public async Task<JsonElement> HostCallAsync(PluginSandboxHostCall request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _inbound.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            return await CallHostAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw Failed(error);
        }
        finally { _inbound.Release(); }
    }

    public async Task<PluginSandboxAck> LogAsync(PluginSandboxLog request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _inbound.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            Log(request);
            return new PluginSandboxAck();
        }
        catch (Exception error)
        {
            _owner.Diagnostic($"A plugin log was rejected: {error.Message}");
            throw Failed(error);
        }
        finally { _inbound.Release(); }
    }

    private static JsonRpcException Failed(Exception error)
        => error as JsonRpcException ?? new JsonRpcException((int)JsonRpcErrorCode.InternalError, error.Message);

    private PluginSandboxSupervisor.Binding Binding(string package, bool checkAuthority = true)
    {
        if (checkAuthority)
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
            var lease = binding.Host.RegisterTool(definition,
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
            var result = await SendAsync<JsonElement>(PluginSandboxMethods.Invoke,
                new PluginSandboxInvokeRequest(binding.Package, tool, arguments, token), linked.Token).ConfigureAwait(false);
            _owner.CheckAuthority(_domain, binding.Package);
            return result;
        }
        finally { _invocations.TryRemove(token, out _); }
    }

    private async Task<JsonElement> CallHostAsync(PluginSandboxHostCall request, CancellationToken cancellationToken)
    {
        var binding = Binding(request.Package);
        if (request.Invocation is not { } token)
            return await binding.Host.CallAsync(request.Operation, request.Arguments,
                null, cancellationToken).ConfigureAwait(false);
        if (!_invocations.TryGetValue(token, out var invocation) || !ReferenceEquals(invocation.Binding, binding))
            throw new UnauthorizedAccessException("The invocation token is stale or belongs to another package.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, invocation.Cancellation.Token);
        return await binding.Host.CallAsync(request.Operation, request.Arguments,
            invocation.Execution, linked.Token).ConfigureAwait(false);
    }

    private void Log(PluginSandboxLog message)
        => Binding(message.Package, checkAuthority: false).Host.Log(message.Level, message.Message);

    /** 四条 broker 方法按线协议名登记, 不为此定义只实现一次的接口。 */
    private (string Method, Delegate Handler)[] Methods() =>
    [
        (PluginSandboxMethods.RegisterTool, (Func<PluginSandboxToolRequest, CancellationToken, Task<PluginSandboxAck>>)RegisterToolAsync),
        (PluginSandboxMethods.UnregisterTool, (Func<PluginSandboxUnregisterTool, CancellationToken, Task<PluginSandboxAck>>)UnregisterToolAsync),
        (PluginSandboxMethods.HostCall, (Func<PluginSandboxHostCall, CancellationToken, Task<JsonElement>>)HostCallAsync),
        (PluginSandboxMethods.Log, (Func<PluginSandboxLog, CancellationToken, Task<PluginSandboxAck>>)LogAsync),
    ];

    /** 单次出向调用: 受 16 并发上限约束, worker 已退役后不再发。 */
    private async Task<T> SendAsync<T>(string method, object request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _outbound.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _aborted) != 0)
                throw new IOException("The isolated worker is no longer available.");
            using var cancellation = cancellationToken.Register(() => Fault("The shared plugin worker operation was cancelled or timed out."));
            return await (_peer ?? throw new IOException("The isolated protocol is unavailable.")).InvokeAsync<T>(method, linked.Token, request).ConfigureAwait(false);
        }
        finally { _outbound.Release(); }
    }

    internal Task<PluginSandboxAck> LoadAsync(PluginSandboxLoadRequest request, CancellationToken cancellationToken)
        => SendAsync<PluginSandboxAck>(PluginSandboxMethods.Load, request, cancellationToken);

    internal Task<PluginSandboxInspection> InspectAsync(PluginSandboxInspectRequest request, CancellationToken cancellationToken)
        => SendAsync<PluginSandboxInspection>(PluginSandboxMethods.Inspect, request, cancellationToken);

    internal Task<PluginSandboxAck> UnloadAsync(PluginSandboxUnloadRequest request, CancellationToken cancellationToken)
        => SendAsync<PluginSandboxAck>(PluginSandboxMethods.Unload, request, cancellationToken);

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
        var peerCleanup = Task.CompletedTask;
        _peer?.Dispose();
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
