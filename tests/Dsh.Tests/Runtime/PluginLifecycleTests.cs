using Dsh.Runtime;

namespace Dsh.Tests.Runtime;

public class PluginLifecycleTests
{
    [Fact]
    public async Task Lifecycle_TransitionsThroughActivation()
    {
        var ctx = new Context();
        var activation = ctx.Plugin(PluginDefinition.From((pluginCtx, _) =>
        {
            pluginCtx.Provide("lifecycle-service", "v");
            return null;
        }, "lifecycle"));
        await activation.WaitAsync();

        Assert.Equal(ActivationState.Active, activation.State);
        Assert.True(activation.IsActive);
        Assert.False(activation.IsTransitioning);
    }

    [Fact]
    public async Task Lifecycle_FailedStaysFailedUntilRetry()
    {
        var ctx = new Context();
        var attempts = 0;
        var activation = ctx.Plugin(PluginDefinition.From((_, _) =>
        {
            attempts++;
            throw new InvalidOperationException("first failure");
        }, "flaky"));
        await activation.WaitAsync();
        Assert.Equal(ActivationState.Failed, activation.State);
        Assert.Equal("first failure", activation.Error);

        // 失败后不会自动重试:再次 settle 不改变状态与尝试次数
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(ActivationState.Failed, activation.State);
        Assert.Equal(1, attempts);

        activation.SendInput(new PluginActivationInput(PluginActivationInputKind.Retry));
        await ctx.Scheduler.SettleAsync();
        Assert.Equal(ActivationState.Failed, activation.State);
        Assert.Equal(2, attempts);
        Assert.Equal("first failure", activation.Error);
    }

    [Fact]
    public async Task Lifecycle_ConcurrentInputsStayConsistent()
    {
        var ctx = new Context();
        var applied = 0;
        var activation = ctx.Plugin(PluginDefinition.From((_, _) =>
        {
            Interlocked.Increment(ref applied);
            return null;
        }, "concurrent"));
        await activation.WaitAsync();

        var inputs = Enumerable.Range(0, 32).Select(index => Task.Run(() =>
        {
            if (index % 2 == 0)
                activation.SendInput(new PluginActivationInput(PluginActivationInputKind.DependencyChanged));
            else
                activation.SendInput(new PluginActivationInput(PluginActivationInputKind.ConfigChanged));
        })).ToArray();
        await Task.WhenAll(inputs);
        await ctx.Scheduler.SettleAsync();

        Assert.Equal(ActivationState.Active, activation.State);
        Assert.False(activation.IsTransitioning);
        Assert.True(applied >= 2);
    }

    [Fact]
    public async Task Lifecycle_DeactivateWithUnloadDisposesEffects()
    {
        var ctx = new Context();
        var disposed = false;
        var activation = ctx.Plugin(PluginDefinition.From((_, _) =>
        {
            return (Action)(() => disposed = true);
        }, "disposable"));
        await activation.WaitAsync();
        Assert.False(disposed);

        await activation.DeactivateAsync();

        Assert.True(disposed);
        Assert.Equal(ActivationState.Disposed, activation.State);
    }

    [Fact]
    public async Task Lifecycle_UnloadWithoutEffectsStillSettles()
    {
        var ctx = new Context();
        var activation = ctx.Plugin(PluginDefinition.From((_, _) => null, "bare"));
        await activation.WaitAsync();
        await activation.DeactivateAsync();
        Assert.Equal(ActivationState.Disposed, activation.State);
        Assert.True(activation.WaitAsync().IsCompletedSuccessfully);
    }
}
