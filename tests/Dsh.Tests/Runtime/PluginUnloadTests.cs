using System.Reflection;
using System.Runtime.CompilerServices;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Tests.Runtime;

public class PluginUnloadTests
{
    [Fact]
    public async Task DynamicPlugin_UnloadCollectsLoadContext()
    {
        var ctx = new Context();
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.Checkpoints.dll");
        Assert.True(File.Exists(pluginPath), $"plugin assembly not found: {pluginPath}");
        var weak = await LoadActivateUnloadAsync(ctx, pluginPath);
        Assert.True(PluginUnloader.WaitForCollection(weak, out var report), report);
    }

    [Fact]
    public async Task DynamicPlugin_UnloadRemovesProvidedServices()
    {
        var ctx = new Context();
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.Checkpoints.dll");
        var (activation, provided) = await LoadAndUnloadTrackedAsync(ctx, pluginPath);
        Assert.Equal(ActivationState.Disposed, activation.State);
        foreach (var name in provided)
            Assert.Null(ctx.Get(name, strict: false));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadActivateUnloadAsync(Context ctx, string pluginPath)
    {
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        Assert.Contains("@deepseek-ai/dsh-checkpoints", loaded.Packages);
        var definition = host.Catalog.CreateDefinition("@deepseek-ai/dsh-checkpoints");
        var activation = await ctx.Scheduler.AddAsync(definition);
        await ctx.Scheduler.UnloadAsync(activation.Name);
        Assert.Equal(ActivationState.Disposed, activation.State);
        host.Catalog.Remove(activation.Name);
        return PluginUnloader.Unload(loaded.Context!);
    }

    [Fact]
    public async Task LeakedReference_IsReportedTruthfully()
    {
        var ctx = new Context();
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.Tests.dll");
        var weak = await LoadAndPinAsync(ctx, pluginPath);
        try
        {
            Assert.False(PluginUnloader.WaitForCollection(weak, out var report));
            Assert.Contains("still referenced", report);
        }
        finally
        {
            GC.KeepAlive(_pinnedAssembly);
            _pinnedAssembly = null;
        }
    }

    private static Assembly? _pinnedAssembly;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadAndPinAsync(Context ctx, string pluginPath)
    {
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        var definition = host.Catalog.CreateDefinition("test/local");
        var activation = await ctx.Scheduler.AddAsync(definition);
        await ctx.Scheduler.UnloadAsync(activation.Name);
        host.Catalog.Remove(activation.Name);
        _pinnedAssembly = loaded.Context!.Assemblies.First(candidate => candidate.GetName().Name == "Dsh.Tests");
        return PluginUnloader.Unload(loaded.Context);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(PluginActivation Activation, IReadOnlyList<string> Provided)> LoadAndUnloadTrackedAsync(Context ctx, string pluginPath)
    {
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        Assert.Contains("@deepseek-ai/dsh-checkpoints", loaded.Packages);
        var definition = host.Catalog.CreateDefinition("@deepseek-ai/dsh-checkpoints");
        var activation = await ctx.Scheduler.AddAsync(definition);
        var provided = activation.ProvidedNames.ToList();
        await ctx.Scheduler.UnloadAsync(activation.Name);
        host.Catalog.Remove(activation.Name);
        _ = PluginUnloader.Unload(loaded.Context!);
        return (activation, provided);
    }
}
