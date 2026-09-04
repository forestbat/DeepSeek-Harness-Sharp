using Cordis.Node;
using Xunit;

namespace Cordis.Tests;

public class NodeBridgeTests : IAsyncLifetime
{
    private NodeHost? _host;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _host?.Dispose();
        return Task.CompletedTask;
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private NodeHost StartHost()
    {
        _host ??= NodeHost.Start();
        return _host;
    }

    [Fact]
    public async Task JsPlugin_EventRoundtrip()
    {
        var host = StartHost();
        var importer = new NodeImporter(host);
        var plugin = await importer.Import(FixturePath("ping-plugin.mjs"), null);
        var ctx = new Context();
        host.RootContext = ctx;
        var fiber = ctx.Plugin(plugin!);
        await fiber.Await();
        Assert.Equal(FiberState.Active, fiber.State);

        var result = await ctx.Events.Serial(null, "ping", 41L);
        Assert.Equal(42L, result);

        var asyncResult = await ctx.Events.Serial(null, "ping/async", 21L);
        Assert.Equal(42L, asyncResult);

        var echo = await ctx.Events.Serial(null, "ping/echo-args", "x", 7L);
        var dict = Assert.IsType<Dictionary<string, object?>>(echo);
        Assert.Equal("x", dict["a"]);
        Assert.Equal(7L, dict["b"]);
    }

    [Fact]
    public async Task JsPlugin_LogsIntoCordisLogger()
    {
        var host = StartHost();
        var importer = new NodeImporter(host);
        var plugin = await importer.Import(FixturePath("ping-plugin.mjs"), null);
        var ctx = new Context();
        host.RootContext = ctx;
        var fiber = ctx.Plugin(plugin!);
        await fiber.Await();
        Assert.Contains(ctx.Logger.Buffer, m => m.Args.Any(a => a?.ToString()?.Contains("ping plugin applied") == true));
    }

    [Fact]
    public async Task JsPlugin_DisposeCallsJsDispose()
    {
        var host = StartHost();
        var importer = new NodeImporter(host);
        var plugin = await importer.Import(FixturePath("ping-plugin.mjs"), null);
        var ctx = new Context();
        host.RootContext = ctx;
        var fiber = ctx.Plugin(plugin!);
        await fiber.Await();
        await fiber.Dispose();
        Assert.Contains(ctx.Logger.Buffer, m => m.Args.Any(a => a?.ToString()?.Contains("ping plugin disposed") == true));
    }
}
