using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Tests.Runtime;

public class RuntimePluginTests
{
    [Fact]
    public async Task Plugin_AppliesAndDisposes()
    {
        var ctx = new Context();
        var events = new List<string>();
        var definition = PluginDefinition.From((_, _) =>
        {
            events.Add("apply");
            return (Action)(() => events.Add("dispose"));
        }, "test-plugin");
        var activation = ctx.Plugin(definition);
        await activation.WaitAsync();
        Assert.Equal(ActivationState.Active, activation.State);
        Assert.Equal(["apply"], events);
        await activation.DeactivateAsync();
        Assert.Equal(["apply", "dispose"], events);
        Assert.Equal(ActivationState.Disposed, activation.State);
    }

    [Fact]
    public async Task Plugin_KeepsWaitingWithoutInject()
    {
        var ctx = new Context();
        var definition = PluginDefinition.From((_, _) => null, "needy", ["foo"]);
        var activation = ctx.Plugin(definition);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(ActivationState.Pending, activation.State);
    }

    [Fact]
    public async Task Plugin_ActivatesWhenInjectProvided()
    {
        var ctx = new Context();
        var applied = 0;
        var definition = PluginDefinition.From((_, _) =>
        {
            applied++;
            return null;
        }, "needy", ["foo"]);
        var activation = ctx.Plugin(definition);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(ActivationState.Pending, activation.State);
        ctx.Provide("foo", 42);
        await activation.WaitAsync();
        Assert.Equal(ActivationState.Active, activation.State);
        Assert.Equal(1, applied);
        Assert.Equal(42, activation.Ctx.Get("foo"));
    }

    [Fact]
    public async Task Plugin_ConfigFlows()
    {
        var ctx = new Context();
        object? received = null;
        var definition = PluginDefinition.From((_, config) =>
        {
            received = config;
            return null;
        });
        await ctx.Plugin(definition, new Dictionary<string, object?> { ["answer"] = 42L }).WaitAsync();
        Assert.Equal(42L, (received as IDictionary<string, object?>)?["answer"]);
    }

    [Fact]
    public async Task Plugin_FailureIsRecorded()
    {
        var ctx = new Context();
        var definition = PluginDefinition.From((_, _) => throw new InvalidOperationException("boom"), "broken");
        var activation = ctx.Plugin(definition);
        await activation.WaitAsync();
        Assert.Equal(ActivationState.Failed, activation.State);
        Assert.Equal("boom", activation.Error);
        Assert.Contains(ctx.Root.Logger.Buffer, message => message.Type == LoggerType.Error);
    }

    [Fact]
    public async Task Plugin_EffectsAreDisposedOnDeactivate()
    {
        var ctx = new Context();
        var received = 0;
        var definition = PluginDefinition.From((pluginCtx, _) =>
        {
            pluginCtx.Provide("service", "value");
            pluginCtx.On<PingNotification>(_ => received++);
            return null;
        }, "owner");
        var activation = ctx.Plugin(definition);
        await activation.WaitAsync();

        ctx.Emit(new PingNotification(1));
        Assert.Equal(1, received);
        Assert.NotNull(ctx.Get("service"));

        await activation.DeactivateAsync();
        ctx.Emit(new PingNotification(2));
        Assert.Equal(1, received);
        Assert.Null(ctx.Get("service"));
    }

    [Fact]
    public void Plugin_RejectsIndefiniteWaitOnceSettled()
    {
        var ctx = new Context();
        var activation = ctx.Plugin(PluginDefinition.From((_, _) => null, "simple"));
        Assert.True(activation.WaitAsync().IsCompletedSuccessfully);
    }
}
