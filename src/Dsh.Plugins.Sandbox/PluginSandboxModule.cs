using Dsh.Core;
using Dsh.Runtime;

namespace Dsh.Plugins.Sandbox;

/// <summary>Connects the optional plugin sandbox implementation to the application composition root.</summary>
public static class PluginSandboxModule
{
    [DshPluginInitializer]
    public static void Register()
        => PluginExecutionRuntime.Factory = CreateRuntime;

    private static IPluginExecutionRuntime CreateRuntime(Context context, IPluginExecutionEnvironment environment,
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
            var catalog = environment.ReadSettings().SandboxEnabled
                ? new PluginSandboxCatalog(environment, host, directory, nativeBridge,
                    new PluginSandboxSupervisor(context, environment, capabilities, directory))
                : null;
            return new Services(capabilities, catalog);
        }
        catch
        {
            capabilities.Dispose();
            throw;
        }
    }

    private sealed class Services(PluginCapabilityFactory capabilities, IPluginExecutionCatalog? catalog)
        : IPluginExecutionRuntime
    {
        public IPluginExecutionCatalog? Catalog => catalog;

        public void Dispose()
        {
            try { Catalog?.Dispose(); }
            finally { capabilities.Dispose(); }
        }
    }
}
