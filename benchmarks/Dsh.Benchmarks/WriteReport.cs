using System.Globalization;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Benchmarks;

/**
 * 落盘体积与帧数报表。
 *
 * 这不是基准: 文件字节数与帧数都是**确定性**的量, 不随时间抖动, 用 BenchmarkDotNet 量它们
 * 只会把 BDN 的时间统计挤掉(它不显示返回值)。所以单独走一条显式命令:
 *
 *   dotnet run -c Release --project benchmarks/Dsh.Benchmarks -- --report
 *
 * 参数扫描的意义: 帧预算决定"一帧装多少内容", 而帧大小同时决定压缩比(帧越大越省)与随机访问代价
 * (帧越小解压越少)。两个方向相反的代价, 只能靠扫描找拐点。
 */
public static class WriteReport
{
    private sealed record Row(
        SessionShape Shape, int PlaintextBytes, long LogBytes, int Frames, long IndexBytes, long Events);

    private static readonly int[] Budgets = [128 * 1024, 256 * 1024, 512 * 1024, 1024 * 1024, 4 * 1024 * 1024];

    public static void Run(TextWriter output)
    {
        foreach (var shape in Enum.GetValues<SessionShape>())
        {
            var events = SessionFactory.Build(shape, 50_000);
            foreach (var budget in Budgets)
                output.WriteLine(Format(Measure(shape, budget, events)));
        }
    }

    private static Row Measure(SessionShape shape, int plaintextBytes, IReadOnlyList<SessionEvent> events)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-bench-size-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var id = SessionFactory.NewSessionId();
            var batch = new JsonlWriteBatch(1_000_000, TimeSpan.FromMilliseconds(100))
            {
                MaxPlaintextBytes = plaintextBytes,
            };
            using (var store = new JsonlSessionPersistence(root, writeBatch: batch))
            using (var handle = store.Create(SessionFactory.Header(id)))
            {
                foreach (var sessionEvent in events)
                    handle.Append([sessionEvent]);
                handle.Flush();
                handle.Close();
            }
            var log = Directory.EnumerateFiles(root, "session.jsonl*", SearchOption.AllDirectories)
                .Single(path => !path.EndsWith(SessionLogIndex.Extension, StringComparison.Ordinal));
            var index = SessionLogIndex.TryLoad(log);
            var indexPath = SessionLogIndex.PathFor(log);
            return new Row(
                shape,
                plaintextBytes,
                new FileInfo(log).Length,
                index?.Frames.Count ?? 0,
                File.Exists(indexPath) ? new FileInfo(indexPath).Length : 0,
                events.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Format(Row row) => string.Create(CultureInfo.InvariantCulture,
        $"{row.Shape,-16} budget={row.PlaintextBytes / 1024,5}KB events={row.Events,7} "
        + $"log={row.LogBytes / 1024d / 1024d,8:N2}MB frames={row.Frames,7} "
        + $"events/frame={(row.Frames == 0 ? 0 : row.Events / (double)row.Frames),8:N1} "
        + $"index={row.IndexBytes / 1024d,9:N1}KB");
}