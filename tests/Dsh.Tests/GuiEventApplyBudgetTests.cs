using System.Diagnostics;
using Avalonia.Threading;
using Dsh.Gui.Services;

namespace Dsh.Tests;

/**
 * 事件应用按帧预算让出。
 *
 * 诊断报告(plans/长会话加载三问-诊断-2026-11-18.md)实测: 1,019,514 条事件被拆成 947,603 批(1.08 条/批)、
 * 补历史阶段 UI 线程单次连续占用 1,750ms、16ms 心跳 p50 1,208ms。判据是"一拍里干完一帧的量就还回去":
 * 于是(1)不再一条一批, (2)一次连续占用不超过一帧量级, (3)让出期间 Input 优先级的操作排得进来。
 */
[Collection(GuiSerialCollection.CollectionName)]
public sealed class GuiEventApplyBudgetTests
{
    private const int Total = 20_000;

    /** 单条事件的模拟成本: 让 20,000 条摊成 100 多拍, 而不是一拍到底。 */
    private const double CostPerEventMs = 0.1;

    private const int HeartbeatPeriodMs = 16;

    [Fact(Timeout = 180_000)]
    public Task BurstIsAppliedInFrameSizedChunks() => HeadlessGui.RunAsync(async () =>
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var applied = 0;
        var batches = new List<int>();
        var longestSliceMs = 0.0;
        var lastEventApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new DispatcherBridge<int>(batch =>
        {
            var watch = Stopwatch.StartNew();
            Burn(CostPerEventMs * batch.Count);
            applied += batch.Count;
            batches.Add(batch.Count);
            longestSliceMs = Math.Max(longestSliceMs, watch.Elapsed.TotalMilliseconds);
            if (batch[^1] == Total - 1)
                lastEventApplied.TrySetResult();
        });

        var lags = new List<double>();
        var heartbeat = new Timer(_ =>
        {
            var issued = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
            {
                var wait = (Stopwatch.GetTimestamp() - issued) * 1000.0 / Stopwatch.Frequency;
                lock (lags)
                    lags.Add(wait);
            }, DispatcherPriority.Input);
        }, null, HeartbeatPeriodMs, HeartbeatPeriodMs);

        var producer = Task.Run(() =>
        {
            for (var index = 0; index < Total; index += 1)
                bridge.Enqueue(index);
        });
        await producer;

        await lastEventApplied.Task.WaitAsync(TimeSpan.FromSeconds(120), cancellationToken);
        heartbeat.Dispose();
        // 让最后几拍心跳落地, 否则统计只剩前半段。
        await Task.Delay(100, cancellationToken);

        Assert.Equal(Total, applied);
        Assert.True(batches.Count > 20, $"只拆出 {batches.Count} 批: 说明一拍干到底, 没有让出(applied={applied} longest={longestSliceMs:0}ms)");
        var average = (double)Total / batches.Count;
        Assert.True(average > 8, $"平均 {average:0.0} 条/批: 仍是几条一批, 批次没有按预算聚合");
        Assert.True(longestSliceMs < 60, $"单次连续占用 {longestSliceMs:0}ms: 超过一帧预算的量级");

        List<double> samples;
        lock (lags)
            samples = [.. lags];
        Assert.True(samples.Count > 20, $"心跳样本只有 {samples.Count} 个");
        var sorted = samples.Order().ToList();
        var p95 = sorted[Math.Clamp((int)(sorted.Count * 0.95), 0, sorted.Count - 1)];
        var max = sorted[^1];
        TestContext.Current.SendDiagnosticMessage(
            $"批次={batches.Count} 平均={average:0.0}条/批 单次最长={longestSliceMs:0}ms " +
            $"心跳 样本={samples.Count} p50={sorted[sorted.Count / 2]:0}ms p95={p95:0}ms max={max:0}ms");
        Assert.True(p95 < 100, $"心跳 p95={p95:0}ms: UI 线程被占住超过 100ms");
        Assert.True(max < 400, $"心跳 max={max:0}ms: 出现过秒级的连续占用");
    }, TimeSpan.FromMinutes(3));

    /** 模拟"处理一条事件要花的时间": 忙等比 Sleep 精确, 也不会把线程交出去。 */
    private static void Burn(double milliseconds)
    {
        var until = Stopwatch.GetTimestamp() + (long)(milliseconds * Stopwatch.Frequency / 1000.0);
        while (Stopwatch.GetTimestamp() < until)
            Thread.SpinWait(16);
    }
}