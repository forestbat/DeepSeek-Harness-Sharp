using Dsh.Runtime;

namespace Dsh.Tests;

public sealed class PluginApplyAsyncTests
{
    [Fact(Timeout = 5000)]
    public async Task AsyncApply_KeepsActivationPendingUntilWorkCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ctx = new Context();
        var activation = ctx.Plugin(new PluginDefinition
        {
            Name = "async-apply",
            Apply = async (context, _, _) =>
            {
                await gate.Task.WaitAsync(ct);
                context.Provide("async-apply-ready", new object());
                return null;
            },
        });

        await Task.Yield();
        Assert.Equal(ActivationState.Activating, activation.State);
        Assert.Null(ctx.Get("async-apply-ready", false));

        gate.SetResult();
        await activation.WaitAsync();
        Assert.Equal(ActivationState.Active, activation.State);
        Assert.NotNull(ctx.Get("async-apply-ready", false));
    }

    [Fact(Timeout = 5000)]
    public async Task UnloadDuringActivation_DisposesEffectsAndReturnedHandle()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = 0;
        var ctx = new Context();
        var activation = ctx.Plugin(new PluginDefinition
        {
            Name = "unload-during-apply",
            Apply = async (context, _, _) =>
            {
                context.Provide("late-service", new object());
                entered.TrySetResult();
                // 故意不观察取消 token: 宿主必须在 Apply 结束后自行清理本次 activation 的效应。
                await proceed.Task.WaitAsync(ct);
                return (Action)(() => Interlocked.Increment(ref disposed));
            },
        });

        await entered.Task.WaitAsync(ct);
        var unload = activation.DeactivateAsync();
        proceed.SetResult();
        await unload.WaitAsync(ct);

        Assert.Equal(ActivationState.Disposed, activation.State);
        Assert.Null(ctx.Get("late-service", false));
        Assert.Equal(1, disposed);
    }

    [Fact(Timeout = 5000)]
    public async Task CanceledActivation_CannotPublishServiceAfterUnload()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? publishError = null;
        var ctx = new Context();
        var activation = ctx.Plugin(new PluginDefinition
        {
            Name = "canceled-apply",
            Apply = async (context, _, _) =>
            {
                entered.TrySetResult();
                await proceed.Task.WaitAsync(ct);
                publishError = Record.Exception(() => context.Provide("post-cancel", new object()));
                return null;
            },
        });

        await entered.Task.WaitAsync(ct);
        var unload = activation.DeactivateAsync();
        proceed.SetResult();
        await unload.WaitAsync(ct);

        Assert.IsType<RuntimeException>(publishError);
        Assert.Null(ctx.Get("post-cancel", false));
        Assert.Equal(ActivationState.Disposed, activation.State);
    }

    [Fact(Timeout = 5000)]
    public async Task IndependentApplies_RunInParallel()
    {
        var ct = TestContext.Current.CancellationToken;
        var entered = 0;
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<object?> Apply(Context context, object? _, CancellationToken token)
        {
            if (Interlocked.Increment(ref entered) == 2)
                bothEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return null;
        }
        var ctx = new Context();
        var first = ctx.Plugin(new PluginDefinition { Name = "parallel-a", Apply = Apply });
        var second = ctx.Plugin(new PluginDefinition { Name = "parallel-b", Apply = Apply });

        // 串行执行时第二个 Apply 永远进不来, 这里会等到测试超时。
        await bothEntered.Task.WaitAsync(ct);
        release.SetResult();
        await Task.WhenAll(first.WaitAsync(), second.WaitAsync()).WaitAsync(ct);

        Assert.Equal(ActivationState.Active, first.State);
        Assert.Equal(ActivationState.Active, second.State);
    }
}
