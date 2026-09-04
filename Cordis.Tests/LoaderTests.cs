using Cordis.Loader;
using Xunit;

namespace Cordis.Tests;

public class LoaderTests
{
    private sealed class DictImporter(Dictionary<string, object?> modules) : IModuleImporter
    {
        public Task<object?> Import(string specifier, string? baseUrl)
        {
            if (modules.TryGetValue(specifier, out var plugin)) return Task.FromResult(plugin);
            throw new CordisException("NOT_FOUND", $"module {specifier} not found");
        }

        public ValueTask<object?> Evaluate(Context ctx, string expr)
        {
            throw new CordisException("NO_EVAL", "eval not supported by dict importer");
        }
    }

    private static (Context ctx, Cordis.Loader.Loader loader) CreateLoader(Dictionary<string, object?> modules, string? baseUrl = null)
    {
        var ctx = new Context { BaseUrl = baseUrl ?? "file:///tmp/cordis-test/" };
        var loader = new Cordis.Loader.Loader(ctx)
        {
            Importer = new DictImporter(modules),
        };
        loader.Builtins["group"] = typeof(Group);
        return (ctx, loader);
    }

    [Fact]
    public async Task Loader_AppliesEntry()
    {
        var applied = 0;
        var modules = new Dictionary<string, object?>
        {
            ["plugin-a"] = PluginDefinition.From((ctx, config) =>
            {
                applied++;
                return null;
            }, "plugin-a"),
        };
        var (ctx, loader) = CreateLoader(modules);
        await loader.Root.Update([new EntryOptions { Id = "a", Name = "plugin-a" }]);
        await loader.Await();
        Assert.Equal(1, applied);
        var entry = loader.Resolve("a");
        Assert.NotNull(entry.Fiber);
        Assert.Equal(FiberState.Active, entry.Fiber!.State);
    }

    [Fact]
    public async Task Loader_DisabledEntryNotApplied()
    {
        var applied = 0;
        var modules = new Dictionary<string, object?>
        {
            ["plugin-a"] = PluginDefinition.From((ctx, config) =>
            {
                applied++;
                return null;
            }, "plugin-a"),
        };
        var (ctx, loader) = CreateLoader(modules);
        await loader.Root.Update([new EntryOptions { Id = "a", Name = "plugin-a", Disabled = true }]);
        await loader.Await();
        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task Loader_GroupAppliesChildren()
    {
        var applied = new List<string>();
        var modules = new Dictionary<string, object?>
        {
            ["plugin-a"] = PluginDefinition.From((ctx, config) =>
            {
                applied.Add("a");
                return null;
            }, "plugin-a"),
            ["plugin-b"] = PluginDefinition.From((ctx, config) =>
            {
                applied.Add("b");
                return null;
            }, "plugin-b"),
        };
        var (ctx, loader) = CreateLoader(modules);
        await loader.Root.Update([
            new EntryOptions
            {
                Id = "g",
                Name = "cordis:group",
                Group = true,
                Config = new List<object?>
                {
                    new Dictionary<string, object?> { ["id"] = "a", ["name"] = "plugin-a" },
                    new Dictionary<string, object?> { ["id"] = "b", ["name"] = "plugin-b" },
                },
            },
        ]);
        await loader.Await();
        Assert.Equal(2, applied.Count);
        Assert.Contains("a", applied);
        Assert.Contains("b", applied);
    }

    [Fact]
    public async Task Loader_GroupDisposeStopsChildren()
    {
        var disposed = new List<string>();
        var modules = new Dictionary<string, object?>
        {
            ["plugin-a"] = PluginDefinition.From((ctx, config) =>
            {
                return () => disposed.Add("a");
            }, "plugin-a"),
        };
        var (ctx, loader) = CreateLoader(modules);
        await loader.Root.Update([
            new EntryOptions
            {
                Id = "g",
                Name = "cordis:group",
                Group = true,
                Config = new List<object?>
                {
                    new Dictionary<string, object?> { ["id"] = "a", ["name"] = "plugin-a" },
                },
            },
        ]);
        await loader.Await();
        loader.Root.Remove("g");
        await WaitUntil(() => disposed.Contains("a"));
        Assert.Contains("a", disposed);
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("condition not met within timeout");
            }
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Loader_IsolateRealmScopesService()
    {
        var modules = new Dictionary<string, object?>
        {
            ["provider"] = PluginDefinition.From((ctx, config) =>
            {
                ctx.Provide("scoped", "inner-value");
                return null;
            }, "provider"),
        };
        var (ctx, loader) = CreateLoader(modules);
        await loader.Root.Update([
            new EntryOptions
            {
                Id = "g",
                Name = "cordis:group",
                Group = true,
                Isolate = new Dictionary<string, object?> { ["scoped"] = true },
                Config = new List<object?>
                {
                    new Dictionary<string, object?> { ["id"] = "p", ["name"] = "provider" },
                },
            },
        ]);
        await loader.Await();
        // realm 内部可见
        var groupEntry = loader.Resolve("g");
        Assert.NotNull(groupEntry.Subgroup);
        // realm 外（根上下文）不可见
        Assert.Null(ctx.Get("scoped"));
    }

    [Fact]
    public async Task Loader_IncludeLoadsYaml()
    {
        var applied = new List<string>();
        var modules = new Dictionary<string, object?>
        {
            ["plugin-yaml"] = PluginDefinition.From((ctx, config) =>
            {
                applied.Add((config as IDictionary<string, object?>)?.GetOrNull("tag") as string ?? "none");
                return null;
            }, "plugin-yaml"),
        };
        var tempDir = Path.Combine(AppContext.BaseDirectory, "tmp", $"cordis-include-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var yamlPath = Path.Combine(tempDir, "test.cordis.yaml");
            await File.WriteAllTextAsync(yamlPath, """
                - id: one
                  name: plugin-yaml
                  config:
                    tag: first
                - id: two
                  name: plugin-yaml
                  disabled: true
                  config:
                    tag: second
                """);
            var (ctx, loader) = CreateLoader(modules, new Uri(tempDir + "/").AbsoluteUri);
            loader.Builtins["include"] = typeof(Include);
            var fiber = ctx.Plugin(typeof(Include), new Dictionary<string, object?>
            {
                ["path"] = "test.cordis.yaml",
            });
            await fiber.Await();
            await loader.Await();
            Assert.Equal(["first"], applied);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
