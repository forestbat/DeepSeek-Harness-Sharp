using Cordis.Loader;
using Cordis.Node;
using Xunit;

namespace Cordis.Tests;

public class NodeLoaderTests : IDisposable
{
    private NodeHost? _host;

    public void Dispose()
    {
        _host?.Dispose();
    }

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public async Task Loader_LoadsYamlWithJsPluginAndJsExpr()
    {
        var testDir = Path.Combine(AppContext.BaseDirectory, "tmp", $"loader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(testDir, "test.cordis.yml"), """
                - id: ping
                  name: ./ping-plugin.mjs
                  disabled: !!js process.platform === 'win32'
                - id: ping-off
                  name: ./ping-plugin.mjs
                  disabled: !!js process.platform !== 'win32'
                """);
            File.Copy(FixturePath("ping-plugin.mjs"), Path.Combine(testDir, "ping-plugin.mjs"));

            _host = NodeHost.Start();
            var ctx = new Context { BaseUrl = new Uri(testDir + "/").AbsoluteUri };
            _host.RootContext = ctx;
            var loader = new Cordis.Loader.Loader(ctx);
            loader.Attach(_host);
            loader.Builtins["group"] = typeof(Group);
            loader.Builtins["include"] = typeof(Include);

            var appliedEntries = new List<string?>();
            ctx.On("internal/plugin", (thisArg, args) =>
            {
                var f = (Fiber)args[0]!;
                if (f.Entry is not null) appliedEntries.Add(f.Entry.Options.Id);
                return new ValueTask<object?>();
            }, new EventOptions { Global = true });

            var fiber = ctx.Plugin(typeof(Include), new Dictionary<string, object?>
            {
                ["path"] = "test.cordis.yml",
            });
            await fiber.Await();
            await loader.Await();

            // !!js 求值：非 win32 平台上 ping 启用、ping-off 禁用
            Assert.Contains("ping", appliedEntries);
            Assert.DoesNotContain("ping-off", appliedEntries);

            var result = await ctx.Events.Serial(null, "ping", 41L);
            Assert.Equal(42L, result);
        }
        finally
        {
            Directory.Delete(testDir, true);
        }
    }

    [Fact]
    public async Task EternalMinimal_FiltersToolCatalog()
    {
        var pluginPath = "/tmp/kilo/dsh-anchored-standard/eternal-minimal/eternal-minimal.mjs";
        if (!File.Exists(pluginPath)) return;

        _host = NodeHost.Start();
        var ctx = new Context();
        _host.RootContext = ctx;
        ctx.Provide("tools", new object());

        var importer = new NodeImporter(_host);
        var plugin = await importer.Import(pluginPath, null);
        var fiber = ctx.Plugin(plugin!, new Dictionary<string, object?> { ["gateway"] = false });
        await fiber.Await();
        Assert.Equal(FiberState.Active, fiber.State);

        var assembly = new Dictionary<string, object?>
        {
            ["system"] = "base prompt",
            ["tools"] = new List<object?>
            {
                new Dictionary<string, object?> { ["name"] = "bash", ["description"] = "shell" },
                new Dictionary<string, object?> { ["name"] = "str_replace_editor", ["description"] = "editor" },
                new Dictionary<string, object?> { ["name"] = "web_search", ["description"] = "web" },
            },
        };
        var result = await ctx.Events.Waterfall(null, "system-prompt/assemble", [assembly, new Dictionary<string, object?>()],
            () => new ValueTask<object?>(assembly));
        var filtered = Assert.IsType<Dictionary<string, object?>>(result);
        var tools = Assert.IsType<List<object?>>(filtered["tools"]);
        var names = tools.Select(t => ((Dictionary<string, object?>)t)["name"] as string).ToList();
        Assert.Equal(["bash", "str_replace_editor"], names);
    }
}
