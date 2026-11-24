using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.RemoteHost;

namespace Dsh.Bench;

/**
 * 打开会话的代价基准: 用同一份程序跑两套 GC, 看 Satori GC 能不能把"百万条事件"的分配与停顿压下去。
 *
 * 只量真正贵的那两段(实测占 19–31s 的绝大部分): 把日志解析成事件, 再把一页事件序列化成线缆载荷并反序列化回来。
 * GUI 侧的投影只要 94–555ms, 不在这里。用法: Dsh.Bench <sessionsRoot> [会话 id ...]
 */
internal static class Program
{
    private const int PageEvents = 20_000;

    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("用法: Dsh.Bench <sessionsRoot> [会话 id ...]");
            return 2;
        }
        var root = args[0];
        var sessions = args.Length > 1 ? args[1..] : Discover(root);
        if (sessions.Length == 0)
        {
            Console.Error.WriteLine($"在 {root} 下没有找到会话日志");
            return 2;
        }
        Console.WriteLine($"GC: {DescribeGc()}");
        Console.WriteLine();
        Console.WriteLine("| 会话 | 事件数 | 解析 | 解析分配 | JSON 序列化 | 序列化分配 | JSON 反序列化 | 载荷 | GC0/1/2 | 停顿 | 峰值常驻 |");
        Console.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var id in sessions)
            Console.WriteLine(Measure(root, id));
        return 0;
    }

    private static string Measure(string root, string id)
    {
        var sessionId = SessionId.Create(id);
        using var persistence = new JsonlSessionPersistence(root);
        var start = Metric.Capture();

        var parse = Stopwatch.StartNew();
        using var handle = persistence.Open(sessionId, SessionAccess.Read);
        var events = handle.Read();
        parse.Stop();
        var parsed = Metric.Capture();

        var serialize = Stopwatch.StartNew();
        var page = new RemoteSessionSnapshot(handle.Header, [.. events.Take(PageEvents)], events.Count);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(page, HostProtocolJsonContext.Default.RemoteSessionSnapshot);
        serialize.Stop();
        var serialized = Metric.Capture();

        var deserialize = Stopwatch.StartNew();
        var roundTripped = JsonSerializer.Deserialize(bytes, HostProtocolJsonContext.Default.RemoteSessionSnapshot);
        deserialize.Stop();
        var done = Metric.Capture();
        if (roundTripped is null || roundTripped.Events.Count != page.Events.Count)
            throw new InvalidOperationException($"{id}: 反序列化后的条数与序列化前不一致");

        return string.Create(CultureInfo.InvariantCulture,
            $"| {id[..8]} | {events.Count} | {parse.ElapsedMilliseconds}ms | {AllocatedMb(start, parsed)}MB "
            + $"| {serialize.ElapsedMilliseconds}ms | {AllocatedMb(parsed, serialized)}MB "
            + $"| {deserialize.ElapsedMilliseconds}ms | {bytes.Length / 1024 / 1024}MB "
            + $"| {done.Gen0 - start.Gen0}/{done.Gen1 - start.Gen1}/{done.Gen2 - start.Gen2} "
            + $"| {(done.PauseMs - start.PauseMs):0}ms | {done.PeakResidentMb}MB |");
    }

    private static long AllocatedMb(Metric from, Metric to) => (to.Allocated - from.Allocated) / 1024 / 1024;

    private static string[] Discover(string root)
    {
        if (!Directory.Exists(root))
            return [];
        return [.. Directory.EnumerateDirectories(root)
            .SelectMany(Directory.EnumerateDirectories)
            .Where(directory => File.Exists(Path.Combine(directory, "session.jsonl.zstd")))
            .Select(directory => Path.GetFileName(directory))
            .OrderBy(name => name, StringComparer.Ordinal)];
    }

    private static string DescribeGc()
    {
        var info = GC.GetGCMemoryInfo();
        var mode = System.Runtime.GCSettings.IsServerGC ? "server" : "workstation";
        var latency = System.Runtime.GCSettings.LatencyMode;
        return $"{mode}, latency={latency}, totalAvailable={info.TotalAvailableMemoryBytes / 1024 / 1024}MB, "
            + $"BGC={info.Concurrent}";
    }

    /** 一次采样: 分配总量、三代回收次数、累计停顿、峰值常驻。 */
    private readonly record struct Metric(long Allocated, int Gen0, int Gen1, int Gen2, double PauseMs, long PeakResidentMb)
    {
        public static Metric Capture() => new(
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            GC.GetTotalPauseDuration().TotalMilliseconds,
            Process.GetCurrentProcess().PeakWorkingSet64 / 1024 / 1024);
    }
}