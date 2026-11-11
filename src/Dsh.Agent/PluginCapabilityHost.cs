using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Llm;
using Dsh.Plugins;
using Dsh.Runtime;
using Dsh.Agent;
using Dsh.Core;
using Dsh.Safety;

namespace Dsh.Safety;
    public sealed class PluginCapabilityHost(
        Func<string, string, JsonElement, ToolRunContext?, CancellationToken, ValueTask<JsonElement>> invoke,
        string package, Context pluginContext) : IPluginHost, IDisposable
    {
        private readonly Lock _sync = new();
        private readonly Dictionary<int, ToolLease> _registrations = [];
        private int _nextHandle;
        private readonly AsyncLocal<Invocation?> _current = new();
        private readonly CancellationTokenSource _lifetime = new();
        private bool _disposed;

        public IDisposable RegisterTool(PluginToolDefinition definition)
            => RegisterTool(definition, (arguments, run, signal) => InvokeDefinitionAsync(definition, arguments, run, signal));

        public IDisposable RegisterTool(PluginToolDefinition definition,
            Func<JsonElement, ToolRunContext, CancellationToken, ValueTask<JsonElement>> execute)
        {
            var schema = definition.Parameters.GetRawText();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_registrations.Count >= PluginHostOperations.MaximumTools)
                    throw new RuntimeException("PLUGIN_REGISTRATION_LIMIT", "The plugin exceeded the tool count limit.");
                var tools = pluginContext.Get<ToolRuntime>(ToolRuntime.ServiceName)
                    ?? throw new InvalidOperationException("The tools service is required to register plugin tools.");
                var registration = tools.RegisterForPlugin(package, CreateDefinition(definition, schema, execute));
                var handle = checked(++_nextHandle);
                var lease = new ToolLease(this, handle, registration);
                _registrations.Add(handle, lease);
                return lease;
            }
        }

        private static ToolDefinition CreateDefinition(PluginToolDefinition definition, string schema,
            Func<JsonElement, ToolRunContext, CancellationToken, ValueTask<JsonElement>> execute) => new()
        {
            Name = definition.Name,
            Description = definition.Description,
            Parameters = JsonNode.Parse(schema)?.AsObject() ?? throw new ArgumentException("Plugin parameters must be an object schema."),
            Output = new ToolOutputDefinition([], (_, value) => [new TextBlock(value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? "" : value.GetRawText())]),
            Execute = async (arguments, execution) => await execute(arguments, execution, execution.Signal).ConfigureAwait(false),
        };

        private async ValueTask<JsonElement> InvokeDefinitionAsync(PluginToolDefinition definition, JsonElement arguments,
            ToolRunContext run, CancellationToken signal)
        {
            Invocation invocation;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                invocation = new Invocation(run, CancellationTokenSource.CreateLinkedTokenSource(signal, _lifetime.Token));
            }
            var previous = _current.Value;
            _current.Value = invocation;
            try
            {
                invocation.Cancellation.Token.ThrowIfCancellationRequested();
                var result = await definition.Execute(arguments, invocation.Cancellation.Token).ConfigureAwait(false);
                invocation.Cancellation.Token.ThrowIfCancellationRequested();
                return result;
            }
            finally
            {
                _current.Value = previous;
                lock (_sync)
                {
                    invocation.Active = false;
                    invocation.Execution = null;
                }
                try { invocation.Cancellation.Cancel(); }
                finally { invocation.Cancellation.Dispose(); }
            }
        }

        public ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments, CancellationToken cancellationToken = default)
            => CallAsync(operation, arguments, null, cancellationToken, useAmbientInvocation: true);

        public ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments, ToolRunContext? run, CancellationToken cancellationToken)
            => CallAsync(operation, arguments, run, cancellationToken, useAmbientInvocation: false);

        private async ValueTask<JsonElement> CallAsync(string operation, JsonElement arguments, ToolRunContext? run,
            CancellationToken cancellationToken, bool useAmbientInvocation)
        {
            CancellationTokenSource linked;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var invocation = useAmbientInvocation && _current.Value is { Active: true } active ? active : null;
                if (useAmbientInvocation)
                    run = invocation?.Execution;
                var signal = invocation?.Cancellation.Token ?? run?.Signal ?? default;
                linked = signal.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken, signal)
                    : CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            }
            using (linked)
                return await invoke(package, operation, arguments, run, linked.Token).ConfigureAwait(false);
        }

        public void Log(int level, string message)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var logger = pluginContext.LoggerFor(package);
            switch (level)
            {
                case 0: logger.Debug("%s", message); break;
                case 2: logger.Warn("%s", message); break;
                case 3: logger.Error("%s", message); break;
                default: logger.Info("%s", message); break;
            }
        }

        private void Release(int handle)
        {
            lock (_sync)
            {
                _registrations.Remove(handle);
            }
        }

        public void Dispose()
        {
            ToolLease[] leases;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                leases = _registrations.Values.ToArray();
                _registrations.Clear();
            }
            try { _lifetime.Cancel(); }
            finally
            {
                _lifetime.Dispose();
                foreach (var lease in leases)
                    lease.Dispose();
            }
        }

        private sealed class Invocation(ToolRunContext execution, CancellationTokenSource cancellation)
        {
            internal ToolRunContext? Execution { get; set; } = execution;
            internal CancellationTokenSource Cancellation { get; } = cancellation;
            internal bool Active { get; set; } = true;
        }

        private sealed class ToolLease(PluginCapabilityHost owner, int handle, IDisposable registration) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return;
                owner.Release(handle);
                registration.Dispose();
            }
        }
    }
