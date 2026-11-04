using Dsh.Interaction;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 上下文占用显示: 取最近一次请求的提示词 token 对照模型窗口, 与累计统计无关。
 * 两边前端共用这份逻辑, 所以这里只测取数与文案, 不测具体控件。
 */
public sealed class ContextUsageTests
{
    private static TokenUsage Usage(double input, double output = 0) => new(input, output);

    [Fact]
    public void Label_Empty_BeforeAnyUsage()
    {
        var usage = new ContextUsage();

        Assert.Equal(ContextUsage.Empty, usage.Label);
    }

    [Fact]
    public void Label_ShowsOccupancyAndPercentage_WhenWindowKnown()
    {
        var usage = new ContextUsage();
        usage.ObserveRequest(128_000);

        usage.ObserveUsage(Usage(12_800, 500));

        Assert.Equal(12_800, usage.LastInputTokens);
        Assert.Equal("上下文 12.8k/128.0k (10%)", usage.Label);
    }

    [Fact]
    public void Label_ReportsOccupancyWithoutPercentage_WhenWindowUnknown()
    {
        var usage = new ContextUsage();
        usage.ObserveRequest(null);

        usage.ObserveUsage(Usage(7_200));

        Assert.Equal("上下文 7.2k", usage.Label);
    }

    /** 上下文窗口被占用的是当前提示词, 所以必须取最近一次请求而不是累加历史。 */
    [Fact]
    public void LastInputTokens_FollowsLatestRequest_InsteadOfAccumulating()
    {
        var usage = new ContextUsage();
        usage.ObserveRequest(128_000);
        usage.ObserveUsage(Usage(5_000));
        usage.ObserveUsage(Usage(9_000));

        Assert.Equal(9_000, usage.LastInputTokens);
        Assert.Equal("上下文 9.0k/128.0k (7%)", usage.Label);
    }

    [Fact]
    public void Window_IsClearedWhenNextRequestHasNoCapacity()
    {
        var usage = new ContextUsage();
        usage.ObserveRequest(128_000);
        usage.ObserveUsage(Usage(1_000));

        usage.ObserveRequest(null);

        Assert.Null(usage.ContextWindow);
        Assert.Equal("上下文 1.0k", usage.Label);
    }

    [Fact]
    public void ZeroWindow_IsTreatedAsUnknown()
    {
        var usage = new ContextUsage();
        usage.ObserveRequest(0);

        Assert.Null(usage.ContextWindow);
    }

    [Fact]
    public void Reset_ClearsWindowAndOccupancy()
    {
        var usage = new ContextUsage();
        usage.ObserveRequest(64_000);
        usage.ObserveUsage(Usage(1_000));

        usage.Reset();

        Assert.Null(usage.ContextWindow);
        Assert.Equal(0, usage.LastInputTokens);
        Assert.Equal(ContextUsage.Empty, usage.Label);
    }

    [Theory]
    [InlineData(999, "999")]
    [InlineData(1_000, "1.0k")]
    [InlineData(12_345, "12.3k")]
    [InlineData(1_500_000, "1.5M")]
    public void Compact_FormatsTokenCounts(double value, string expected)
    {
        Assert.Equal(expected, TokenText.Compact(value));
    }
}
