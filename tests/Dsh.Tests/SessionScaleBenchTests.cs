using System.Diagnostics;
using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 会话规模基准: 合成 1k/10k/100k 事件的会话(按现场比例, 绝大多数是流式 chunk), 量
 * 写盘耗时、文件大小、首次载入、缓存命中后的再读、以及常驻托管堆的增量。
 * 这是"窗口化要不要做、做在哪一层"的判据, 数字写进 plans/会话窗口化-....md。
 */
[Trait("Category", "Bench")]
public sealed class SessionScaleBenchTests
{
    private const int TurnCount = 4;

    [Theory]
    [InlineData(JsonlCompression.None, 1_000)]
    [InlineData(JsonlCompression.None, 10_000)]
    [InlineData(JsonlCompression.Zstd, 1_000)]
    [InlineData(JsonlCompression.Zstd, 10_000)]
    [InlineData(JsonlCompression.Zstd, 100_000)]
    public void LoadCost_GrowsLinearlyWithEventCount(JsonlCompression compression, int eventCount)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-scale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var header = Header(Path.Combine(root, "workspace"));
            var writeClock = Stopwatch.StartNew();
            using (var writer = new JsonlSessionPersistence(root, compression: compression).Create(header))
                writer.Append(BuildEvents(header, eventCount));
            var writeMs = writeClock.ElapsedMilliseconds;
            var logPath = Directory.GetFiles(root, "session.jsonl*", SearchOption.AllDirectories)
                .Single(path => !path.EndsWith(SessionLogIndex.Extension, StringComparison.Ordinal));
            var fileBytes = new FileInfo(logPath).Length;

            var loadMs = 0L;
            var repeatMs = 0L;
            var loaded = 0;
            var snapshotBytes = 0L;
            using (var store = new JsonlSessionPersistence(root, compression: compression))
            {
                var clock = Stopwatch.StartNew();
                using var handle = store.Open(header.Id, SessionAccess.Read);
                var events = handle.Read();
                loadMs = clock.ElapsedMilliseconds;
                loaded = events.Count;
                snapshotBytes = GC.GetTotalMemory(forceFullCollection: true);
                clock.Restart();
                _ = handle.Read();
                repeatMs = clock.ElapsedMilliseconds;
            }

            TestContext.Current.SendDiagnosticMessage(
                $"{compression,-4} events={loaded,7} write={writeMs,6}ms file={fileBytes / 1024d,9:N1}KB " +
                $"open+read={loadMs,6}ms cached={repeatMs,4}ms heapAfterLoad={snapshotBytes / 1024d / 1024d,7:N1}MB");
            Assert.Equal(eventCount, loaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void WindowedRead_BeatsFullParseForTailAndMiddleWindows()
    {
        const int eventCount = 200_000;
        var root = Path.Combine(Path.GetTempPath(), $"dsh-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var header = Header(Path.Combine(root, "workspace"));
            var all = BuildEvents(header, eventCount);
            using (var writer = new JsonlSessionPersistence(root).Create(header))
            {
                // 按落盘批次大小分批追加: 现场会话就是这么长的(每批一个帧), 一次性写完只会得到 2 个帧。
                for (var offset = 0; offset < all.Count; offset += 1000)
                    writer.Append(all.Skip(offset).Take(1000).ToArray());
            }
            var logPath = Directory.GetFiles(root, "session.jsonl*", SearchOption.AllDirectories)
                .First(path => !path.EndsWith(SessionLogIndex.Extension, StringComparison.Ordinal));
            var fileMb = new FileInfo(logPath).Length / 1024d / 1024d;

            // 冷缓存全量读
            long fullMs;
            int fullCount;
            using (var store = new JsonlSessionPersistence(root))
            {
                var clock = Stopwatch.StartNew();
                using var handle = store.Open(header.Id, SessionAccess.Read);
                fullCount = handle.Read().Count;
                fullMs = clock.ElapsedMilliseconds;
            }

            // 冷缓存取尾窗与中段窗
            var tailMs = TimeWindow(root, header.Id, eventCount - 200, 200, out var tailCount);
            var middleMs = TimeWindow(root, header.Id, eventCount / 2, 200, out var middleCount);
            TestContext.Current.SendDiagnosticMessage(
                $"windowed read: full {fullCount} in {fullMs}ms | tail 200 in {tailMs}ms ({tailCount}) | " +
                $"middle 200 in {middleMs}ms ({middleCount}) | file {fileMb:N1}MB index={(SessionLogIndex.TryLoad(logPath) is { } index ? $"{index.Frames.Count} frames" : "none")}");
            Assert.Equal(eventCount, fullCount);
            Assert.Equal(200, tailCount);
            Assert.Equal(200, middleCount);
            // 取一窗的代价必须远低于全量展开, 否则窗口化没有意义。
            Assert.True(tailMs * 10 < Math.Max(10, fullMs), $"窗口读取 {tailMs}ms 没有显著低于全量 {fullMs}ms");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static long TimeWindow(string root, SessionId id, long offset, int count, out int read)
    {
        using var store = new JsonlSessionPersistence(root);
        var clock = Stopwatch.StartNew();
        using var handle = store.Open(id, SessionAccess.Read);
        read = handle.Read(offset, count).Count;
        return clock.ElapsedMilliseconds;
    }

    [Fact]
    public void SnapshotSerialization_IsTheMultiplierBehindTheFileSize()
    {
        const int eventCount = 10_000;
        var events = BuildEvents(Header(Path.Combine(Path.GetTempPath(), "dsh-scale-snapshot")), eventCount);
        var frames = JsonSerializer.SerializeToUtf8Bytes(events, DshJson.Options);
        var (turnStart, chunkStart, textLength) = Profile(events);
        TestContext.Current.SendDiagnosticMessage(
            $"snapshot of {eventCount} events = {frames.Length / 1024d / 1024d:N1}MB " +
            $"(turn/start={turnStart}, chunk={chunkStart}, text chars={textLength})");
    }

    private static SessionHeader Header(string cwd) => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        Cwd = cwd,
        CreatedAt = 0,
        IsSeeded = false,
    };

    /** 现场比例: 每回合 1 条 turn/start + 1 条 turn/end, 其余按 chunk 与消息交错铺开。 */
    private static List<SessionEvent> BuildEvents(SessionHeader header, int count)
    {
        var session = Session.Create(header.Id, null, header);
        var payloads = new List<SessionEventPayload>(count);
        var perTurn = Math.Max(4, count / TurnCount);
        for (var turn = 1; payloads.Count < count; turn += 1)
        {
            payloads.Add(new TurnStartPayload(turn));
            var step = 1;
            while (payloads.Count < count && payloads.Count % perTurn != 0)
            {
                var text = $"turn {turn} step {step}: 行内容 {payloads.Count}\n";
                payloads.Add(new AssistantChunkPayload(turn, step, new StreamChunk.TextDelta(0, text)));
                if (payloads.Count % 64 == 0)
                    step += 1;
            }
            if (payloads.Count < count)
                payloads.Add(new TurnEndPayload(turn, new TurnEndReason.Completed()));
        }
        foreach (var payload in payloads)
            session.Append(payload);
        return [.. session.SnapshotEvents()];
    }

    private static (int TurnStarts, int Chunks, int TextChars) Profile(IReadOnlyList<SessionEvent> events)
    {
        var turnStarts = 0;
        var chunks = 0;
        var chars = 0;
        foreach (var sessionEvent in events)
        {
            switch (sessionEvent.Data)
            {
                case TurnStartPayload:
                    turnStarts += 1;
                    break;
                case AssistantChunkPayload { Chunk: StreamChunk.TextDelta delta }:
                    chunks += 1;
                    chars += delta.Text.Length;
                    break;
            }
        }
        return (turnStarts, chunks, chars);
    }
}