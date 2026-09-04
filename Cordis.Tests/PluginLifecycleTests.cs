using Xunit;

namespace Cordis.Tests;

public class PluginLifecycleTests
{
    [Fact]
    public async Task Plugin_LoadsAndDisposes()
    {
        var ctx = new Context();
        var events = new List<string>();
        var plugin = PluginDefinition.From((ctx, config) =>
        {
            events.Add("apply");
            return () => events.Add("dispose");
        }, "test-plugin");
        var fiber = ctx.Plugin(plugin);
        await fiber.Await();
        Assert.Equal(FiberState.Active, fiber.State);
        Assert.Equal(["apply"], events);
        await fiber.Dispose();
        Assert.Equal(["apply", "dispose"], events);
        Assert.Equal(FiberState.Disposed, fiber.State);
    }

    [Fact]
    public async Task Plugin_KeepsWaitingWithoutInject()
    {
        var ctx = new Context();
        var plugin = new PluginDefinition
        {
            Name = "needy",
            Inject = new Dictionary<string, object?> { ["foo"] = null },
            Callback = new DelegatePluginCallback((ctx, config) => null),
        };
        var fiber = ctx.Plugin(plugin);
        await Task.Delay(50);
        Assert.Equal(FiberState.Pending, fiber.State);
    }

    [Fact]
    public async Task Plugin_ActivatesWhenInjectProvided()
    {
        var ctx = new Context();
        var applied = 0;
        var plugin = new PluginDefinition
        {
            Name = "needy",
            Inject = new Dictionary<string, object?> { ["foo"] = null },
            Callback = new DelegatePluginCallback((ctx, config) =>
            {
                applied++;
                return null;
            }),
        };
        var fiber = ctx.Plugin(plugin);
        await Task.Delay(20);
        Assert.Equal(FiberState.Pending, fiber.State);
        ctx.Provide("foo", 42);
        await fiber.Await();
        Assert.Equal(FiberState.Active, fiber.State);
        Assert.Equal(1, applied);
        Assert.Equal(42, fiber.Ctx.Get("foo"));
    }

    [Fact]
    public async Task Plugin_UnloadsWhenInjectDisposed()
    {
        var ctx = new Context();
        var states = new List<FiberState>();
        var plugin = new PluginDefinition
        {
            Name = "needy",
            Inject = new Dictionary<string, object?> { ["foo"] = null },
            Callback = new DelegatePluginCallback((ctx, config) => null),
        };
        var fiber = ctx.Plugin(plugin);
        var provide = ctx.Provide("foo", 42);
        await fiber.Await();
        Assert.Equal(FiberState.Active, fiber.State);
        await provide.DisposeAsync();
        await Task.Delay(50);
        Assert.Equal(FiberState.Pending, fiber.State);
    }

    [Fact]
    public async Task Plugin_ConfigFlows()
    {
        var ctx = new Context();
        object? received = null;
        var plugin = PluginDefinition.From((ctx, config) =>
        {
            received = config;
            return null;
        });
        await ctx.Plugin(plugin, new Dictionary<string, object?> { ["answer"] = 42L }).Await();
        Assert.Equal(42L, (received as IDictionary<string, object?>)?["answer"]);
    }

    [Fact]
    public async Task Plugin_ClassForm_WithAsyncInit()
    {
        var ctx = new Context();
        var fiber = ctx.Plugin(typeof(ClassPlugin), null);
        await fiber.Await();
        Assert.Equal(FiberState.Active, fiber.State);
        Assert.True(ClassPlugin.Constructed);
        await fiber.Dispose();
        Assert.True(ClassPlugin.Disposed);
    }

    private sealed class ClassPlugin : IAsyncInit
    {
        public static bool Constructed;
        public static bool Disposed;

        public ClassPlugin(Context ctx, object? config)
        {
            Constructed = true;
        }

        public async IAsyncEnumerable<object?> Init()
        {
            yield return (Action)(() => Disposed = true);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Registry_DeleteDisposesAllFibers()
    {
        var ctx = new Context();
        var plugin = PluginDefinition.From((ctx, config) => null, "shared");
        var fiber1 = ctx.Plugin(plugin, 1L);
        var fiber2 = ctx.Plugin(plugin, 2L);
        await fiber1.Await();
        await fiber2.Await();
        Assert.True(ctx.Registry.Has(plugin));
        ctx.Registry.Delete(plugin);
        await Task.Delay(50);
        Assert.False(ctx.Registry.Has(plugin));
        Assert.Equal(FiberState.Disposed, fiber1.State);
        Assert.Equal(FiberState.Disposed, fiber2.State);
    }

    [Fact]
    public void Plugin_RejectsInvalidPlugin()
    {
        var ctx = new Context();
        Assert.Throws<CordisException>(() => ctx.Plugin(42L));
    }
}
