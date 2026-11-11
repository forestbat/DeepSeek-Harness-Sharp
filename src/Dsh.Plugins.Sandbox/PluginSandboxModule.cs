using Dsh.Boot;
using Dsh.Core;
using Dsh.Runtime;
using Dsh.Subprocess;
using Dsh.Agent;

namespace Dsh.Plugins.Sandbox;

/// <summary>Connects the optional plugin sandbox implementation to the application composition root.</summary>
public static class PluginSandboxModule
{
    public static PluginHost? CreateRuntime(Context context, PluginExecutionEnvironment environment,
        string directory, Func<INativePlugin, IDshPlugin>? nativeBridge, Action<IDisposable> track)
    {
        var capabilities = new PluginCapabilityFactory(context, environment);
        try { track(capabilities); }
        catch
        {
            capabilities.Dispose();
            throw;
        }
        context.Provide(SubprocessService.ScriptLauncherServiceName,
            (Func<SubprocessService, SubprocessSpawnSpec, Task<SubprocessHandle>>)new ScriptExecutionPolicy(environment).SpawnAsync);
        context.Plugin(new PluginDefinition
        {
            Name = IPluginHostFactory.ServiceName,
            Inject = [ToolRuntime.ServiceName],
            Apply = (pluginContext, _, _) => Task.FromResult<object?>(
                pluginContext.Provide(IPluginHostFactory.ServiceName, capabilities)),
        });
        if (!environment.ReadSettings().SandboxEnabled)
            return null;
        var host = new PluginSandboxHost(context, environment, directory, nativeBridge, capabilities);
        try { track(host); }
        catch
        {
            host.Dispose();
            throw;
        }
        return host;
    }


    /// <summary>Returns null for ordinary arguments without creating an application context.</summary>
    public static ValueTask<int>? TryRunWorkerAsync(string[] args) => PluginSandboxWorker.TryRunAsync(args);
}
