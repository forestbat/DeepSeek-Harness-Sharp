using Cordis.Loader;
using Cordis.Plugins;

namespace Cordis.Tests;

public class TimerBuiltinTests
{
    [Fact]
    public async Task Timer_RegisteredAsLoaderBuiltin()
    {
        var ctx = new Context { BaseUrl = "file:///tmp/cordis-test/" };
        var loader = new Cordis.Loader.Loader(ctx);
        loader.Builtins["group"] = typeof(Group);
        loader.Builtins["timer"] = typeof(TimerService);

        await loader.Root.Update([
            new EntryOptions { Id = "timer", Name = "cordis:timer" },
        ]);
        await loader.Await();

        Assert.IsType<TimerService>(ctx.Get("timer"));
    }
}
