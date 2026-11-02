using System.Runtime.CompilerServices;
using Dsh.Plugins;
using Dsh.Runtime;

namespace Dsh.Tests;

[Collection(SettingsTemplateTests.RegistryCollection)]
public class ThirdPartyEventTests
{
    [Fact]
    public async Task CollectibleAssembly_RegistersHandlerAndReceivesItsOwnNotification()
    {
        var ctx = new Context();
        var pluginPath = Path.Combine(AppContext.BaseDirectory, "Dsh.Tests.dll");
        Assert.True(File.Exists(pluginPath), $"plugin assembly not found: {pluginPath}");

        var received = await LoadAndActivateAsync(ctx, pluginPath);

        Assert.Equal("third-party-hello", received);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<string?> LoadAndActivateAsync(Context ctx, string pluginPath)
    {
        var host = new PluginHost();
        var loaded = host.TryLoad(pluginPath);
        Assert.Contains("test/local", loaded.Packages);
        var definition = host.Catalog.CreateDefinition("test/local");
        var activation = await ctx.Scheduler.AddAsync(definition);
        Assert.Equal(ActivationState.Active, activation.State);
        var assembly = loaded.Context!.Assemblies.First(candidate => candidate.GetName().Name == "Dsh.Tests");
        var pluginType = assembly.GetType("Dsh.Tests.TestPlugin")!;
        return (string?)pluginType.GetProperty("ReceivedText")!.GetValue(null);
    }
}
