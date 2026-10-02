using Dsh.Runtime;
using Dsh.Runtime.Composition;

namespace Dsh.Tests.Runtime;

/** 类型化注入: InjectTypes 依赖边(拓扑/pending/重建)与 ctx.Get 泛型重载的类型解析。 */
public class TypedInjectionTests
{
    public interface IGreeter
    {
        string Greet();
    }

    private sealed class Greeter(string text) : IGreeter
    {
        public string Greet() => text;
    }

    private static PluginDefinition Provider(string name, string service, IGreeter greeter)
        => PluginDefinition.From((ctx, _) =>
        {
            ctx.Provide(service, greeter);
            return null;
        }, name);

    [Fact]
    public async Task TypedEdge_ActivatesAfterProviderRegardlessOfRegistrationOrder()
    {
        var applied = new List<IGreeter>();
        var consumer = PluginDefinition.From((ctx, _) =>
        {
            applied.Add(ctx.Get<IGreeter>()!);
            return null;
        }, "consumer", injectTypes: [typeof(IGreeter)]);
        var ctx = new Context();
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(consumer, null),
            new PluginEntry(Provider("provider", "greeter", new Greeter("hi")), null),
        ]);

        Assert.Equal(ActivationState.Active, composition.Find("consumer")!.State);
        Assert.Single(applied);
        Assert.Equal("hi", applied[0].Greet());
    }

    [Fact]
    public async Task TypedEdge_PendingWhenMissingAndWarnsWithTypeName()
    {
        var ctx = new Context();
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(PluginDefinition.From((_, _) => null, "consumer", injectTypes: [typeof(IGreeter)]), null),
        ]);

        Assert.Equal(ActivationState.Pending, composition.Find("consumer")!.State);
        Assert.Contains(ctx.Logger.Buffer, message =>
            message.Type == LoggerType.Warn
            && message.Text.Contains("consumer")
            && message.Text.Contains(nameof(IGreeter))
            && message.Text.Contains("no service assignable"));
    }

    [Fact]
    public async Task TypedEdge_PendingWarnListsInactiveOrAmbiguousCandidates()
    {
        var ctx = new Context();
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(PluginDefinition.From((pluginCtx, _) =>
            {
                pluginCtx.Provide("greeter", new Greeter("hi"), check: () => false);
                return null;
            }, "provider"), null),
            new PluginEntry(PluginDefinition.From((_, _) => null, "consumer", injectTypes: [typeof(IGreeter)]), null),
        ]);

        Assert.Equal(ActivationState.Pending, composition.Find("consumer")!.State);
        Assert.Contains(ctx.Logger.Buffer, message =>
            message.Type == LoggerType.Warn
            && message.Text.Contains(nameof(IGreeter))
            && message.Text.Contains("greeter")
            && message.Text.Contains("provider"));
    }

    [Fact]
    public async Task TypedEdge_RebuildsWhenProviderIsReplaced()
    {
        var applied = new List<IGreeter>();
        var consumer = PluginDefinition.From((ctx, _) =>
        {
            applied.Add(ctx.Get<IGreeter>()!);
            return null;
        }, "consumer", injectTypes: [typeof(IGreeter)]);
        var ctx = new Context();
        var composition = await Composition.StartAsync(ctx,
        [
            new PluginEntry(consumer, null),
            new PluginEntry(Provider("provider-a", "greeter", new Greeter("from-a")), null),
        ]);
        Assert.Equal("from-a", Assert.Single(applied).Greet());

        await ctx.Scheduler.UnloadAsync("provider-a");
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(ActivationState.Pending, composition.Find("consumer")!.State);

        await ctx.Scheduler.AddAsync(Provider("provider-b", "greeter", new Greeter("from-b")));
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(ActivationState.Active, composition.Find("consumer")!.State);
        Assert.Equal("from-b", applied[^1].Greet());
    }

    [Fact]
    public async Task GetByType_AmbiguousThrowsAndNameDisambiguates()
    {
        var ctx = new Context();
        await Composition.StartAsync(ctx,
        [
            new PluginEntry(Provider("provider-a", "greeter-a", new Greeter("from-a")), null),
            new PluginEntry(Provider("provider-b", "greeter-b", new Greeter("from-b")), null),
        ]);

        var error = Assert.Throws<RuntimeException>(() => ctx.Get<IGreeter>());
        Assert.Contains("greeter-a", error.Message);
        Assert.Contains("greeter-b", error.Message);
        Assert.Equal("from-a", ctx.Get<IGreeter>("greeter-a")!.Greet());
    }

    [Fact]
    public void GetByType_NoCandidateReturnsNull()
    {
        var ctx = new Context();
        Assert.Null(ctx.Get<IGreeter>());
    }
}
