using System.Diagnostics;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 手机上滑取更早的消息时, host 每回应一次都要解析一份会话日志。
 * 这里量的是这一步的真实成本: 它决定"取一屏"能不能在一秒内返回。
 *
 * 找不到现场的大日志时直接跳过: 这是性能基准, 不该因为开发机没有历史而不通过。
 */
public sealed class SessionLogReadBenchTests
{
    /** 现场最大的会话日志所在的会话目录名; 换机器就不存在, 所以按存在与否跳过。 */
    private const string CanonicalSessionId = "eec210bb-e5d8-4d42-85ae-0de489913d8e";

    [Fact]
    public void ReadingALargeStoredLog_RepeatsCheaplyOnceParsed()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "sessions");
        if (FindLog(root) is not { } log)
            return;
        var sizeMb = new FileInfo(log).Length / (1024d * 1024d);
        using var store = new JsonlSessionPersistence(root);
        var id = SessionId.Create(CanonicalSessionId);

        var costs = new List<(long Open, long Read, int Events)>();
        for (var round = 0; round < 3; round += 1)
        {
            var clock = Stopwatch.StartNew();
            using var handle = store.Open(id, SessionAccess.Read);
            var open = clock.ElapsedMilliseconds;
            var events = handle.Read();
            costs.Add((open, clock.ElapsedMilliseconds - open, events.Count));
        }
        TestContext.Current.SendDiagnosticMessage(
            $"log {sizeMb:N1} MB: " + string.Join(" | ", costs.Select(cost => $"open={cost.Open}ms read={cost.Read}ms events={cost.Events}")));

        // 首次解析之后, 同一份未改动的日志不该再花同样多的时间。
        Assert.True(costs[^1].Read < Math.Max(50, costs[0].Read / 2),
            $"重复读取未改动的日志仍然要 {costs[^1].Read}ms, 首次是 {costs[0].Read}ms; 解析结果没有被复用");
    }

    private static string? FindLog(string root)
    {
        if (!Directory.Exists(root))
            return null;
        foreach (var project in Directory.GetDirectories(root))
        {
            var sessionDir = Path.Combine(project, CanonicalSessionId);
            foreach (var name in new[] { "session.jsonl.zstd", "session.jsonl" })
            {
                var path = Path.Combine(sessionDir, name);
                if (File.Exists(path))
                    return path;
            }
        }
        return null;
    }
}
