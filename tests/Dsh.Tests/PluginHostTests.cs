using Cordis;
using Cordis.Loader;
using Dsh.Plugins;

[assembly: DshPlugin("test/local")]

namespace Dsh.Tests;

public sealed class PluginHostTests
{
    [Fact]
    public void RegisterAssembly_TryCreate_AppliesPlugin()
    {
        var catalog = new PluginCatalog();
        catalog.RegisterAssembly(typeof(PluginHostTests).Assembly);

        Assert.True(catalog.TryCreate("test/local", out var plugin));
        var testPlugin = Assert.IsType<TestPlugin>(plugin);
        Assert.Empty(testPlugin.Inject);

        using var registration = testPlugin.Apply(new Context(), null);
        Assert.True(TestPlugin.Applied);
    }

    [Fact]
    public async Task ScanDirectory_RegistersBuiltinAndApplies()
    {
        TestPlugin.Applied = false;
        var host = new PluginHost();
        host.ScanDirectory(AppContext.BaseDirectory);

        Assert.True(host.Catalog.TryCreate("test/local", out _));

        var ctx = new Context();
        var loader = new Loader(ctx);
        host.RegisterBuiltins(loader);

        Assert.True(loader.Builtins.TryGetValue("test/local", out var definition));
        var pluginDefinition = Assert.IsType<PluginDefinition>(definition);
        var fiber = ctx.Plugin(pluginDefinition);
        await fiber.Await();
        Assert.True(TestPlugin.Applied);
    }
}

public sealed class TestPlugin : IDshPlugin
{
    public static bool Applied { get; set; }

    public string[] Inject => [];

    public IDisposable Apply(Context ctx, object? config)
    {
        Applied = true;
        return new NoopDisposable();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}