using Xunit;

namespace Cordis.Tests;

public class EventsTests
{
    [Fact]
    public void Emit_InvokesListenersInOrder()
    {
        var ctx = new Context();
        var calls = new List<string>();
        ctx.On("test", (_, _) =>
        {
            calls.Add("a");
            return new ValueTask<object?>();
        });
        ctx.On("test", (_, _) =>
        {
            calls.Add("b");
            return new ValueTask<object?>();
        }, new EventOptions { Prepend = true });
        ctx.Emit("test");
        Assert.Equal(["b", "a"], calls);
    }

    [Fact]
    public void Once_DisposesAfterFirstCall()
    {
        var ctx = new Context();
        var count = 0;
        ctx.Once("test", (_, _) =>
        {
            count++;
            return new ValueTask<object?>();
        });
        ctx.Emit("test");
        ctx.Emit("test");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Bail_ReturnsFirstNonNullishValue()
    {
        var ctx = new Context();
        ctx.On("test", (_, _) => new ValueTask<object?>());
        ctx.On("test", (_, _) => new ValueTask<object?>(false));
        ctx.On("test", (_, _) => new ValueTask<object?>("late"));
        var result = await ctx.Events.Bail(null, "test");
        Assert.Equal("late", result);
    }

    [Fact]
    public async Task Bail_SkipsNullAndUndefined()
    {
        var ctx = new Context();
        ctx.On("test", (_, _) => new ValueTask<object?>());
        ctx.On("test", (_, _) => new ValueTask<object?>("hit"));
        Assert.Equal("hit", await ctx.Events.Bail(null, "test"));
    }

    [Fact]
    public async Task Serial_AwaitsEachListener()
    {
        var ctx = new Context();
        var calls = new List<string>();
        ctx.On("test", async (_, _) =>
        {
            await Task.Delay(10);
            calls.Add("a");
            return null;
        });
        ctx.On("test", (_, _) =>
        {
            calls.Add("b");
            return new ValueTask<object?>((object?)null);
        });
        await ctx.Events.Serial(null, "test");
        Assert.Equal(["a", "b"], calls);
    }

    [Fact]
    public async Task Waterfall_ChainsNext()
    {
        var ctx = new Context();
        ctx.On("test", async (_, args) =>
        {
            var next = (Func<ValueTask<object?>>)args[1]!;
            var value = await next();
            return $"a{(string?)value}";
        });
        ctx.On("test", async (_, args) =>
        {
            var next = (Func<ValueTask<object?>>)args[1]!;
            var value = await next();
            return $"b{(string?)value}";
        });
        var result = await ctx.Events.Waterfall(null, "test", ["x"], () => new ValueTask<object?>("!"));
        Assert.Equal("ab!", result);
    }

    [Fact]
    public async Task Parallel_ThrowsAggregateException()
    {
        var ctx = new Context();
        ctx.On("test", (_, _) => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<AggregateException>(() => ctx.Events.Parallel(null, "test"));
    }

    [Fact]
    public void Off_DisposeStopsListener()
    {
        var ctx = new Context();
        var count = 0;
        var dispose = ctx.On("test", (_, _) =>
        {
            count++;
            return new ValueTask<object?>();
        });
        ctx.Emit("test");
        dispose();
        ctx.Emit("test");
        Assert.Equal(1, count);
    }
}
