using Cordis.Loader;

namespace Cordis.Tests;

public class FiberUpdateAwaitTests
{
    [Fact]
    public async Task FiberUpdateAsync_AwaitsRestart()
    {
        var applied = 0;
        var modules = new Dictionary<string, object?>
        {
            ["plugin-a"] = PluginDefinition.From((_, _) =>
            {
                applied++;
                return null;
            }, "plugin-a"),
        };
        var (_, loader) = LoaderTests.CreateLoader(modules);
        await loader.Root.Update([
            new EntryOptions
            {
                Id = "a",
                Name = "plugin-a",
                Config = new Dictionary<string, object?> { ["v"] = 1 },
            },
        ]);
        await loader.Await();
        Assert.Equal(1, applied);

        var fiber = loader.Resolve("a").Fiber!;
        await fiber.UpdateAsync(new Dictionary<string, object?> { ["v"] = 2 });

        Assert.Equal(2, applied);
        Assert.Null(fiber.Inertia);
        Assert.Equal(FiberState.Active, fiber.State);
    }

    [Fact]
    public async Task EntryUpdate_AwaitsFiberReload()
    {
        var applied = 0;
        var modules = new Dictionary<string, object?>
        {
            ["plugin-a"] = PluginDefinition.From((_, _) =>
            {
                applied++;
                return null;
            }, "plugin-a"),
        };
        var (_, loader) = LoaderTests.CreateLoader(modules);
        await loader.Root.Update([
            new EntryOptions
            {
                Id = "a",
                Name = "plugin-a",
                Config = new Dictionary<string, object?> { ["v"] = 1 },
            },
        ]);
        await loader.Await();
        Assert.Equal(1, applied);

        await loader.Root.Update([
            new EntryOptions
            {
                Id = "a",
                Name = "plugin-a",
                Config = new Dictionary<string, object?> { ["v"] = 2 },
            },
        ]);
        await loader.Await();

        Assert.Equal(2, applied);
        Assert.Null(loader.Resolve("a").Fiber!.Inertia);
    }
}
