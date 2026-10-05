using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Dsh.Llm;

namespace Dsh.Tests;

/** 防静默中断: 缺终止符的装配、看门狗阈值与 idle/lifetime 行为。 */
public class StreamGuardTests
{
    [Fact]
    public void LlmStreamLimits_DefaultsToQwenCodeThresholds()
    {
        var limits = LlmStreamLimits.Resolve(null, null);
        Assert.Equal(240_000, limits.IdleTimeoutMs);
        Assert.Equal(900_000, limits.MaxLifetimeMs);
        Assert.True(limits.Enabled);
    }

    [Fact]
    public void LlmStreamLimits_ZeroAndNegativeDisable()
    {
        Assert.False(LlmStreamLimits.Resolve(0, 0).Enabled);
        Assert.False(LlmStreamLimits.Resolve(-5, -5).Enabled);
    }

    [Fact]
    public void BlockAssembler_WithoutFinishChunk_IsIncompleteInsteadOfStop()
    {
        var assembler = new BlockAssembler();
        assembler.Push(new StreamChunk.TextDelta(0, "hi"));
        assembler.Push(new StreamChunk.BlockEnd(0, new TextBlock("hi")));

        Assert.IsType<FinishReason.Incomplete>(assembler.Finish);
    }

    [Fact]
    public async Task AutoMapper_WithoutDone_EmitsIncomplete()
    {
        var chunks = await CollectAutoMapperAsync("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n");

        var finish = Assert.IsType<StreamChunk.Finish>(chunks[^1]);
        Assert.IsType<FinishReason.Incomplete>(finish.Reason);
    }

    [Fact]
    public async Task AutoMapper_WithDoneWithoutFinishReason_EmitsStop()
    {
        var chunks = await CollectAutoMapperAsync(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\ndata: [DONE]\n\n");

        var finish = Assert.IsType<StreamChunk.Finish>(chunks[^1]);
        Assert.IsType<FinishReason.Stop>(finish.Reason);
    }

    [Fact]
    public async Task Watchdog_PassesThroughNormalStream()
    {
        using var watchdog = new StreamWatchdog(new LlmStreamLimits(5_000, 5_000), CancellationToken.None);
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in watchdog.Guard(Normal(TestContext.Current.CancellationToken)))
            chunks.Add(chunk);

        var finish = Assert.IsType<StreamChunk.Finish>(chunks[^1]);
        Assert.IsType<FinishReason.Stop>(finish.Reason);
    }

    [Fact]
    public async Task Watchdog_IdleTimeout_EmitsRetryableTimeout()
    {
        using var watchdog = new StreamWatchdog(new LlmStreamLimits(50, 0), CancellationToken.None);
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in watchdog.Guard(Never(TestContext.Current.CancellationToken)))
            chunks.Add(chunk);

        var finish = Assert.IsType<StreamChunk.Finish>(Assert.Single(chunks));
        var error = Assert.IsType<FinishReason.Error>(finish.Reason);
        Assert.Equal(LlmFailureCodes.Timeout, error.Failure.Code);
    }

    [Fact]
    public async Task Watchdog_LifetimeCap_StopsDripFeedEvenThoughIdleNeverTrips()
    {
        using var watchdog = new StreamWatchdog(new LlmStreamLimits(0, 100), CancellationToken.None);
        var started = Stopwatch.GetTimestamp();
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in watchdog.Guard(DripFeed(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken)))
            chunks.Add(chunk);
        var elapsed = Stopwatch.GetElapsedTime(started);

        var finish = Assert.IsType<StreamChunk.Finish>(chunks[^1]);
        var error = Assert.IsType<FinishReason.Error>(finish.Reason);
        Assert.Equal(LlmFailureCodes.Timeout, error.Failure.Code);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"watchdog took too long: {elapsed}");
    }

    /** 上游无视取消令牌时, 看门狗仍然照常产出超时结束, 不阻塞消费端、也不把释放异常抛出来。 */
    [Fact]
    public async Task Watchdog_StillFinishes_WhenUpstreamIgnoresCancellation()
    {
        using var watchdog = new StreamWatchdog(new LlmStreamLimits(50, 0), CancellationToken.None);
        var started = Stopwatch.GetTimestamp();
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in watchdog.Guard(Stubborn()))
            chunks.Add(chunk);
        var elapsed = Stopwatch.GetElapsedTime(started);

        var finish = Assert.IsType<StreamChunk.Finish>(Assert.Single(chunks));
        var error = Assert.IsType<FinishReason.Error>(finish.Reason);
        Assert.Equal(LlmFailureCodes.Timeout, error.Failure.Code);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"watchdog took too long: {elapsed}");
    }

    private static async Task<List<StreamChunk>> CollectAutoMapperAsync(string payload)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        var chunks = new List<StreamChunk>();
        await foreach (var chunk in StreamingResponseAutoMapper.Translate(stream, CancellationToken.None))
            chunks.Add(chunk);
        return chunks;
    }

    private static async IAsyncEnumerable<StreamChunk> Never([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    private static async IAsyncEnumerable<StreamChunk> DripFeed(
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 有限但足够长: 看门狗会在 lifetime 上限处先中止, 不会等到源自然结束。
        for (var index = 0; index < 1_000; index++)
        {
            await Task.Delay(interval, cancellationToken);
            yield return new StreamChunk.TextDelta(0, $"{index}");
        }
    }

    private static async IAsyncEnumerable<StreamChunk> Normal([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return new StreamChunk.TextDelta(0, "hi");
        yield return new StreamChunk.BlockEnd(0, new TextBlock("hi"));
        yield return new StreamChunk.Finish(new FinishReason.Stop());
    }

    /** 顽固上游: 不接收取消令牌, 永不返回, 模拟"无视取消的适配器"。 */
    private static async IAsyncEnumerable<StreamChunk> Stubborn()
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
        yield break;
    }
}
