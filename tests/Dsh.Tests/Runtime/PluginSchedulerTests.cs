using Dsh.Runtime;

namespace Dsh.Tests.Runtime;

public class PluginSchedulerTests
{
    [Fact]
    public async Task Scheduler_ActivatesInDependencyOrderRegardlessOfRegistrationOrder()
    {
        var ctx = new Context();
        var order = new List<string>();
        var definitions = new Dictionary<string, PluginDefinition>(StringComparer.Ordinal)
        {
            ["consumer"] = PluginDefinition.From((c, _) =>
            {
                order.Add("consumer");
                Assert.NotNull(c.Get("svc-b"));
                return null;
            }, "consumer", ["svc-b"]),
            ["middle"] = PluginDefinition.From((c, _) =>
            {
                order.Add("middle");
                Assert.NotNull(c.Get("svc-a"));
                c.Provide("svc-b", "b");
                return null;
            }, "middle", ["svc-a"]),
            ["provider"] = PluginDefinition.From((c, _) =>
            {
                order.Add("provider");
                c.Provide("svc-a", "a");
                return null;
            }, "provider"),
        };

        foreach (var name in new[] { "consumer", "middle", "provider" })
            ctx.Plugin(definitions[name]);
        await ctx.Scheduler.SettleAsync();

        Assert.Equal(["provider", "middle", "consumer"], order);
        Assert.All(ctx.Scheduler.Snapshot(), activation => Assert.Equal(ActivationState.Active, activation.State));
    }

    [Fact]
    public async Task Scheduler_RebuildsDependentsWhenProviderChanges()
    {
        var ctx = new Context();
        var applies = 0;
        ctx.Plugin(PluginDefinition.From((c, _) =>
        {
            c.Provide("svc", "one");
            return null;
        }, "provider-1"));
        var consumer = ctx.Plugin(PluginDefinition.From((c, _) =>
        {
            applies++;
            Assert.NotNull(c.Get("svc"));
            return null;
        }, "consumer", ["svc"]));
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(1, applies);
        var epochBefore = ctx.Scheduler.Epoch;

        var provider = ctx.Scheduler.Find("provider-1")!;
        await provider.DeactivateAsync();
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(1, applies);
        Assert.Equal(ActivationState.Pending, consumer.State);
        Assert.Null(consumer.Ctx.Get("svc"));

        ctx.Plugin(PluginDefinition.From((c, _) =>
        {
            c.Provide("svc", "two");
            return null;
        }, "provider-2"));
        await ctx.Scheduler.SettleAsync();

        Assert.Equal(ActivationState.Active, consumer.State);
        Assert.Equal(2, applies);
        Assert.Equal("two", consumer.Ctx.Get("svc"));
        Assert.True(ctx.Scheduler.Epoch > epochBefore);
    }

    [Fact]
    public async Task Registration_CompletesWhileAnotherProviderIsApplying()
    {
        var ctx = new Context();
        var applied = 0;
        var consumer = ctx.Plugin(PluginDefinition.From((context, _) =>
        {
            Assert.Equal("ready", context.Get("svc"));
            Interlocked.Increment(ref applied);
            return null;
        }, "consumer", ["svc"]));
        await ctx.Scheduler.SettleAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var provider = Task.Factory.StartNew(() => ctx.Plugin(PluginDefinition.From((context, _) =>
        {
            entered.SetResult();
            release.Wait(TestContext.Current.CancellationToken);
            context.Provide("svc", "ready");
            return null;
        }, "provider")), TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await entered.Task;
        var independent = Task.Factory.StartNew(() => ctx.Plugin(PluginDefinition.From(static (_, _) => null, "independent")),
            TestContext.Current.CancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            // A user Apply callback may be slow; publishing another activation cannot wait while holding the scheduler's registry lock.
            var activation = await independent.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await activation.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(ActivationState.Active, activation.State);
            // 无依赖插件必须与仍在 Apply 的插件并行完成, 而不是被它串行化。
            Assert.Equal(ActivationState.Activating, ctx.Scheduler.Find("provider")!.State);
        }
        finally
        {
            release.Set();
            await provider;
            await independent;
        }
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(ActivationState.Active, consumer.State);
        Assert.Equal(1, applied);
        Assert.Equal("ready", consumer.Ctx.Get("svc"));
    }

    [Fact]
    public async Task Scheduler_KeepsDownstreamPendingWhenUpstreamFails()
    {
        var ctx = new Context();
        ctx.Plugin(PluginDefinition.From((c, _) =>
        {
            c.Provide("svc-a", "a");
            return null;
        }, "root"));
        var broken = ctx.Plugin(PluginDefinition.From((_, _) => throw new InvalidOperationException("broken"), "broken", ["svc-a"]));
        var downstream = ctx.Plugin(PluginDefinition.From((_, _) => null, "downstream", ["svc-b"]));
        await ctx.Scheduler.SettleAsync();

        Assert.Equal(ActivationState.Active, ctx.Scheduler.Find("root")!.State);
        Assert.Equal(ActivationState.Failed, broken.State);
        Assert.Equal(ActivationState.Pending, downstream.State);

        // 依赖变更后失败者回到 Pending 重试,但上游依旧不可用则再次失败
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(ActivationState.Failed, broken.State);
        Assert.Equal(ActivationState.Pending, downstream.State);
    }

    [Fact]
    public async Task Scheduler_EpochIsMonotonic()
    {
        var ctx = new Context();
        var epochs = new List<long> { ctx.Scheduler.Epoch };
        ctx.Plugin(PluginDefinition.From((c, _) =>
        {
            c.Provide("svc", "v");
            return null;
        }, "provider"));
        await ctx.Scheduler.SettleAsync();
        epochs.Add(ctx.Scheduler.Epoch);

        ctx.Plugin(PluginDefinition.From((_, _) => null, "consumer", ["svc"]));
        await ctx.Scheduler.SettleAsync();
        epochs.Add(ctx.Scheduler.Epoch);

        for (var index = 1; index < epochs.Count; index++)
            Assert.True(epochs[index] >= epochs[index - 1], string.Join(",", epochs));
        Assert.True(epochs[^1] > epochs[0]);
    }

    [Fact]
    public async Task Scheduler_DeactivatesDependentsBeforeProvider()
    {
        var ctx = new Context();
        var events = new List<string>();
        var provider = ctx.Plugin(PluginDefinition.From((c, _) =>
        {
            c.Provide("svc", "v");
            return (Action)(() => events.Add("provider-disposed"));
        }, "provider"));
        ctx.Plugin(PluginDefinition.From((c, _) =>
        {
            Assert.NotNull(c.Get("svc"));
            return (Action)(() => events.Add("consumer-disposed"));
        }, "consumer", ["svc"]));
        await ctx.Scheduler.SettleAsync();

        await provider.DeactivateAsync();
        await ctx.Scheduler.SettleAsync();

        Assert.Equal(["consumer-disposed", "provider-disposed"], events);
    }
}
