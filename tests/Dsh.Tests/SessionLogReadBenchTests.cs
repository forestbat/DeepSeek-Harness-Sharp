using System.Diagnostics;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 手机上滑取更早的消息时, host 每回应一次都要解析一份会话日志。
 * 这里量的是这一步的真实成本: 它决定"取一屏"能不能在一秒内返回。
 *
 * 拿本机最大的一份现场日志当样本; 找不到就跳过 —— 这是性能基准, 不该因为开发机没有历史而不通过。
 */
[Trait("Category", "Bench")]
public sealed class SessionLogReadBenchTests
{
    [Fact]
    public void ReadingALargeStoredLog_RepeatsCheaplyOnceParsed()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        if (LargestLog(root) is not { } log)
            return;
        var sizeMb = new FileInfo(log).Length / (1024d * 1024d);
        using var store = new JsonlSessionPersistence(root);
        var id = SessionId.Create(Path.GetFileName(Path.GetDirectoryName(log))!);

        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        long windowMs = -1;
        int windowCount = 0;
        var costs = new List<(long Open, long Read, int Events)>();
        for (var round = 0; round < 3; round += 1)
        {
            var clock = Stopwatch.StartNew();
            using var handle = store.Open(id, SessionAccess.Read);
            var open = clock.ElapsedMilliseconds;
            IReadOnlyList<SessionEvent> events;
            try
            {
                events = handle.Read();
            }
            catch (SessionFormatUnsupportedException)
            {
                // 现场日志里带着插件事件(如 todo/write、compaction/prune), 它们的事件编码在插件启动时登记;
                // 本进程没组合过插件目录时读不动这份日志, 基准也就无从谈起 —— 与"找不到现场日志"同样跳过。
                TestContext.Current.SendDiagnosticMessage($"largest log {sizeMb:N1} MB unreadable without its plugins");
                return;
            }
            costs.Add((open, clock.ElapsedMilliseconds - open, events.Count));
        }
        using (var reader = store.Open(id, SessionAccess.Read))
        {
            var total = costs[^1].Events;
            var clock = Stopwatch.StartNew();
            var window = reader.Read(Math.Max(0, total - 100), 100);
            windowMs = clock.ElapsedMilliseconds;
            windowCount = window.Count;
        }
        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        TestContext.Current.SendDiagnosticMessage(
            $"log {sizeMb:N1} MB: " + string.Join(" | ", costs.Select(cost => $"open={cost.Open}ms read={cost.Read}ms events={cost.Events}"))
            + $" | resident={costs[^1].Events} events hold {(heapAfter - heapBefore) / 1024d / 1024d:N0}MB"
            + $" | tail window 100 events = {windowMs}ms ({windowCount} events, index={(SessionLogIndex.TryLoad(log) is null ? "none" : "ok")})");

        // 首次解析之后, 同一份未改动的日志不该再花同样多的时间。
        Assert.True(costs[^1].Read < Math.Max(50, costs[0].Read / 2),
            $"重复读取未改动的日志仍然要 {costs[^1].Read}ms, 首次是 {costs[0].Read}ms; 解析结果没有被复用");
    }

    private static string? LargestLog(string root)
    {
        if (!Directory.Exists(root))
            return null;
        string? largest = null;
        var largestLength = 0L;
        foreach (var path in Directory.EnumerateFiles(root, "session.jsonl*", SearchOption.AllDirectories)
                     .Where(path => !path.EndsWith(SessionLogIndex.Extension, StringComparison.Ordinal)))
        {
            var length = new FileInfo(path).Length;
            if (length <= largestLength)
                continue;
            largest = path;
            largestLength = length;
        }
        return largest;
    }
}