using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Tests.Runtime;

public sealed record PingNotification(int Value) : INotification
{
    public static string EventName => "test/ping";
}

public sealed record VetoNotification(string Reason) : INotification
{
    public static string EventName => "test/veto";
}

public class EventBusTests
{
    [Fact]
    public void Emit_IsolatesErrorsAndContinues()
    {
        var ctx = new Context();
        var seen = new List<string>();
        ctx.On<PingNotification>(_ => throw new InvalidOperationException("boom"));
        ctx.On<PingNotification>(notification => seen.Add($"second:{notification.Value}"));
        ctx.Emit(new PingNotification(7));

        Assert.Equal(["second:7"], seen);
        Assert.Contains(ctx.Root.Logger.Buffer, message => message.Type == LoggerType.Error && message.Text.Contains("boom"));
    }

    [Fact]
    public async Task Emit_ObservesAsyncHandlers()
    {
        var ctx = new Context();
        var seen = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.OnAsync<PingNotification>(async notification =>
        {
            await Task.Yield();
            seen.TrySetResult(notification.Value);
        });

        ctx.Emit(new PingNotification(11));

        Assert.Equal(11, await seen.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Parallel_AggregatesHandlerErrors()
    {
        var ctx = new Context();
        ctx.On<PingNotification>(_ => throw new InvalidOperationException("first"));
        ctx.On<PingNotification>(_ => throw new InvalidOperationException("second"));

        var error = await Assert.ThrowsAsync<AggregateException>(() => ctx.Events.Parallel(new PingNotification(1)));

        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Contains(error.InnerExceptions, inner => inner.Message == "first");
        Assert.Contains(error.InnerExceptions, inner => inner.Message == "second");
    }

    [Fact]
    public async Task Serial_ShortCircuitsOnBail()
    {
        var ctx = new Context();
        var visited = new List<string>();
        ctx.On<VetoNotification>(_ => visited.Add("plain"));
        ctx.OnBail<VetoNotification>(notification =>
        {
            visited.Add("bail");
            return ValueTask.FromResult<object?>(notification.Reason == "stop" ? "vetoed" : null);
        });
        ctx.On<VetoNotification>(_ => visited.Add("after"));

        var result = await ctx.Events.Serial(new VetoNotification("stop"));

        Assert.Equal("vetoed", result);
        Assert.Equal(["plain", "bail"], visited);
    }

    [Fact]
    public async Task Waterfall_ChainsNextAndEnforcesSingleCall()
    {
        var ctx = new Context();
        var visited = new List<string>();
        ctx.OnWaterfall<PingNotification>(async (_, next) =>
        {
            visited.Add("first");
            return await next();
        });
        ctx.OnWaterfall<PingNotification>(async (_, next) =>
        {
            visited.Add("second");
            return await next();
        });

        var result = await ctx.Events.Waterfall(new PingNotification(3), () =>
        {
            visited.Add("inner");
            return ValueTask.FromResult<object?>("inner-result");
        });

        Assert.Equal("inner-result", result);
        Assert.Equal(["first", "second", "inner"], visited);
    }

    [Fact]
    public async Task Waterfall_RejectsDoubleNext()
    {
        var ctx = new Context();
        InvalidOperationException? doubleNext = null;
        ctx.OnWaterfall<PingNotification>((_, next) =>
        {
            try
            {
                var first = next();
                first.GetAwaiter().GetResult();
                next();
            }
            catch (InvalidOperationException error)
            {
                doubleNext = error;
            }
            return ValueTask.FromResult<object?>(null);
        });

        await ctx.Events.Waterfall(new PingNotification(3), () => ValueTask.FromResult<object?>(null));

        Assert.NotNull(doubleNext);
    }

    [Fact]
    public void Options_PrependGlobalAndFilter()
    {
        var ctx = new Context();
        var order = new List<string>();
        ctx.On<PingNotification>(_ => order.Add("normal"));
        ctx.On<PingNotification>(_ => order.Add("prepended"), new EventOptions { Prepend = true });
        ctx.Emit(new PingNotification(1));
        Assert.Equal(["prepended", "normal"], order);

        var filtered = ctx.WithFilter(_ => false);
        var carrier = ctx.WithFilter(candidate => ReferenceEquals(candidate, ctx));
        var ran = false;
        filtered.On<PingNotification>(_ => ran = true);
        ctx.Events.Emit(carrier, new PingNotification(2));
        Assert.False(ran);

        ctx.On<PingNotification>(_ => ran = true);
        ctx.Events.Emit(carrier, new PingNotification(3));
        Assert.True(ran);

        ran = false;
        filtered.On<PingNotification>(_ => ran = true, new EventOptions { Global = true });
        ctx.Events.Emit(carrier, new PingNotification(4));
        Assert.True(ran);
    }

    [Fact]
    public async Task Registration_IsRemovedWithOwnerEffectDisposal()
    {
        var ctx = new Context();
        var seen = 0;
        var activation = ctx.Plugin(PluginDefinition.From((pluginCtx, _) =>
        {
            pluginCtx.Provide("evt-service", "v");
            pluginCtx.On<PingNotification>(_ => seen++);
            return null;
        }, "evt-owner"));
        ctx.Emit(new PingNotification(1));
        Assert.Equal(1, seen);

        await activation.DeactivateAsync();
        ctx.Emit(new PingNotification(2));
        Assert.Equal(1, seen);
    }
}
