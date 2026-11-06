using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Plugins.Sandbox;

/// <summary>Connects the optional plugin sandbox implementation to the application composition root.</summary>
public sealed class PluginSandboxModule : IPluginRuntime
{
    private readonly PluginCapabilityFactory _capabilities;

    private PluginSandboxModule(Context context, IPluginExecutionEnvironment environment,
        PluginHost host, string directory, Func<INativePlugin, IDshPlugin>? nativeBridge)
    {
        var capabilities = new PluginCapabilityFactory(context, environment);
        try
        {
            context.Provide(IScriptExecutionPolicy.ServiceName, new ScriptExecutionPolicy(environment));
            context.Plugin(new PluginDefinition
            {
                Name = IPluginHostFactory.ServiceName,
                Inject = [ToolRuntime.ServiceName],
                Apply = (pluginContext, _, _) => Task.FromResult<object?>(
                    pluginContext.Provide(IPluginHostFactory.ServiceName, capabilities)),
            });
            Catalog = environment.ReadSettings().SandboxEnabled
                ? new PluginSandboxCatalog(environment, host, directory, nativeBridge,
                    new PluginSandboxSupervisor(context, environment, capabilities, directory))
                : null;
            _capabilities = capabilities;
        }
        catch
        {
            capabilities.Dispose();
            throw;
        }
    }

    public IPluginExecutionCatalog? Catalog { get; }

    public static IPluginRuntime CreateRuntime(Context context, IPluginExecutionEnvironment environment,
        PluginHost host, string directory, Func<INativePlugin, IDshPlugin>? nativeBridge)
        => new PluginSandboxModule(context, environment, host, directory, nativeBridge);

    /// <summary>Returns null for ordinary arguments without creating an application context.</summary>
    public static ValueTask<int>? TryRunWorkerAsync(string[] args) => PluginSandboxWorker.TryRunAsync(args);

    public void Dispose()
    {
        try { Catalog?.Dispose(); }
        finally { _capabilities.Dispose(); }
    }
}
