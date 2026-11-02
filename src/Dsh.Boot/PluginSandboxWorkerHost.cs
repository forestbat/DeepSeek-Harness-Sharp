using System.Text.Json;
using Dsh.Plugins;
using Dsh.Transport;
using static Dsh.Boot.PluginSandboxWorkerWire;

namespace Dsh.Boot;

/** Each package has callbacks and broker policy; the shared process is co-trusted. */
internal sealed class PluginSandboxWorkerHost(PluginSandboxWorker.Session session, string package, bool native)
    : IPluginHost, INativePluginHost
{
    private readonly Lock _gate = new();
    private readonly Dictionary<int, Tool> _tools = [];
    private readonly AsyncLocal<PluginSandboxWorker.Invocation?> _current = new();
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private int _nextTool;
    private bool _stopped;
    private int _nativeThread;
    private PluginSandboxWorker.Invocation? _nativeInvocation;

    internal CancellationTokenSource Lifetime { get; } = new();

    public IDisposable RegisterTool(PluginToolDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var id = Interlocked.Increment(ref _nextTool);
        if (id <= 0)
            throw new InvalidOperationException("Worker tool identifiers exhausted.");
        Add(id, definition.Name, definition.Description, definition.Parameters, definition.Execute);
        return new Registration(this, id);
    }

    public void RegisterTool(int handle, string name, string description, string parametersJson, Func<string, string?> invoke)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        var schema = Parse(parametersJson);
        Add(handle, name, description, schema, (arguments, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = invoke(arguments.GetRawText())
                ?? throw new InvalidOperationException("Native plugin tool failed.");
            return ValueTask.FromResult(Parse(result));
        });
    }

    private void Add(int id, string name, string description, JsonElement parameters,
        Func<JsonElement, CancellationToken, ValueTask<JsonElement>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Plugin tool parameters must be a JSON object.");
        var schema = parameters.Clone();
        var request = Serialize(new PluginSandboxToolRequest(package, id, name, description, schema),
            PluginSandboxJsonContext.Default.PluginSandboxToolRequest);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (_tools.ContainsKey(id))
                throw new InvalidOperationException("Duplicate plugin tool identifier.");
            session.ReserveTool();
            _tools.Add(id, new Tool(execute));
            try
            {
                session.Reverse(PluginSandboxMethods.RegisterTool, request, Lifetime.Token).GetAwaiter().GetResult();
            }
            catch
            {
                _tools.Remove(id);
                session.ReleaseTool();
                throw;
            }
        }
    }

    public bool UnregisterTool(int handle)
    {
        lock (_gate)
        {
            if (!_tools.Remove(handle))
                return false;
            session.ReleaseTool();
            session.Reverse(PluginSandboxMethods.UnregisterTool,
                Serialize(new PluginSandboxUnregisterTool(package, handle),
                    PluginSandboxJsonContext.Default.PluginSandboxUnregisterTool), CancellationToken.None)
                .GetAwaiter().GetResult();
            return true;
        }
    }

    public async ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments,
        CancellationToken cancellationToken = default)
    {
        PluginSandboxWorker.Invocation? invocation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            // The optional native callback inherits only the synchronous invocation's context.
            // Background threads have no active tool execution.
            invocation = native
                ? _nativeThread == Environment.CurrentManagedThreadId ? _nativeInvocation : null
                : _current.Value;
        }
        if (invocation is { Active: false })
            invocation = null;
        using var linked = invocation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token, cancellationToken, invocation.Cancellation.Token);
        var request = Serialize(new PluginSandboxHostCall(package, invocation?.Token, operation, Element(arguments)),
            PluginSandboxJsonContext.Default.PluginSandboxHostCall);
        return await session.Reverse(PluginSandboxMethods.HostCall, request, linked.Token).ConfigureAwait(false);
    }

    public string CallHost(string operation, string argumentsJson)
        => CallAsync(operation, Parse(argumentsJson)).AsTask().GetAwaiter().GetResult().GetRawText();

    public void Log(int level, string message)
    {
        lock (_gate)
            ObjectDisposedException.ThrowIf(_stopped, this);
        session.Reverse(PluginSandboxMethods.Log,
            Serialize(new PluginSandboxLog(package, level, message), PluginSandboxJsonContext.Default.PluginSandboxLog),
            Lifetime.Token).GetAwaiter().GetResult();
    }

    internal async Task<JsonElement> InvokeAsync(int id, JsonElement arguments, PluginSandboxWorker.Invocation invocation)
    {
        if (native)
            await _nativeGate.WaitAsync(invocation.Cancellation.Token).ConfigureAwait(false);
        try
        {
            Tool tool;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopped, this);
                if (!_tools.TryGetValue(id, out tool!))
                    throw new InvalidOperationException("Plugin tool is not registered.");
                if (native)
                {
                    _nativeThread = Environment.CurrentManagedThreadId;
                    _nativeInvocation = invocation;
                }
            }
            _current.Value = invocation;
            try
            {
                invocation.Cancellation.Token.ThrowIfCancellationRequested();
                var result = await tool.Execute(arguments, invocation.Cancellation.Token).ConfigureAwait(false);
                invocation.Cancellation.Token.ThrowIfCancellationRequested();
                return Element(result);
            }
            finally
            {
                _current.Value = null;
                lock (_gate)
                {
                    _nativeThread = 0;
                    _nativeInvocation = null;
                }
            }
        }
        finally
        {
            if (native)
                _nativeGate.Release();
        }
    }

    internal void Stop()
    {
        lock (_gate)
            _stopped = true;
        Lifetime.Cancel();
    }

    internal void ReleaseTools()
    {
        KeyValuePair<int, Tool>[] tools;
        lock (_gate)
        {
            tools = [.. _tools];
            _tools.Clear();
        }
        foreach (var (id, _) in tools)
        {
            session.ReleaseTool();
            session.Reverse(PluginSandboxMethods.UnregisterTool,
                Serialize(new PluginSandboxUnregisterTool(package, id),
                    PluginSandboxJsonContext.Default.PluginSandboxUnregisterTool), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
    }

    private sealed record Tool(Func<JsonElement, CancellationToken, ValueTask<JsonElement>> Execute);

    private sealed class Registration(PluginSandboxWorkerHost owner, int id) : IDisposable
    {
        private PluginSandboxWorkerHost? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.UnregisterTool(id);
    }
}
