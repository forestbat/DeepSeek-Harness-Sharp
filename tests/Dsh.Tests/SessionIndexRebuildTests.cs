using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 全量读成功之后补写帧索引: 一次"退回全量打开"不该变成永久退回。
 *
 * 判据三条: 索引缺了, 全量读一次就补回来, 下一次打开走窗口化; 有损伤的日志一个字节的索引都不写。
 */
public sealed class SessionIndexRebuildTests : IDisposable
{
    private const int EventCount = 6_000;
    private const int BatchSize = 500;
    private const int TailEvents = 1_000;
    private readonly List<string> _roots = [];

    [Fact]
    public void FullRead_RebuildsTheIndex_AndTheNextOpenIsWindowed()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        SessionLogIndex.Delete(logPath);
        Assert.Null(SessionLogIndex.TryLoad(logPath));

        long events;
        using (var store = new JsonlSessionPersistence(root))
        {
            using var handle = store.Open(header.Id, SessionAccess.Read);
            events = handle.Read().Count;
        }

        var rebuilt = SessionLogIndex.TryLoad(logPath);
        Assert.True(rebuilt is not null, "全量读之后没有补写帧索引");
        Assert.Equal(events, rebuilt.EventCount);
        Assert.Equal(new FileInfo(logPath).Length, rebuilt.Length);
        Assert.True(rebuilt.Frames.Count > 1, $"索引只有 {rebuilt.Frames.Count} 帧");
        Assert.Equal(0, rebuilt.Frames[0].Start);

        // 换一个进程内的新实例(与"下一次打开"等价): 有了索引就走窗口化。
        var warnings = new List<string>();
        using var reopened = new JsonlSessionPersistence(root, warn: warnings.Add);
        var window = reopened.TryOpenWindow(header.Id, TailEvents);
        Assert.True(window is not null, $"索引重建之后仍然打不开窗口: {string.Join("; ", warnings)}");
        var paged = window.GetValueOrDefault();
        Assert.True(paged.ResidentFrom > 0);
        Assert.Equal(events, paged.TotalEvents);
        Assert.Equal(paged.TotalEvents, paged.ResidentFrom + paged.Resident.Count);
    }

    [Fact]
    public void CorruptFrame_DoesNotWriteAnIndex()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        SessionLogIndex.Delete(logPath);
        CorruptMiddleFrame(logPath);

        using (var store = new JsonlSessionPersistence(root, warn: _ => { }))
        {
            using var handle = store.Open(header.Id, SessionAccess.Read);
            var events = handle.Read();
            Assert.True(events.Count < EventCount, "被改坏那一帧里的事件应当读不出来");
        }

        Assert.Null(SessionLogIndex.TryLoad(logPath));
        Assert.False(File.Exists(SessionLogIndex.PathFor(logPath)), "有损伤的日志不该写索引: 错的帧边界会被固化成索引");
    }

    [Fact]
    public void TornTail_DoesNotWriteAnIndex()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        SessionLogIndex.Delete(logPath);
        // 末尾写了一半的帧(断电/被杀): 帧切不齐, 边界不可信。
        var bytes = File.ReadAllBytes(logPath);
        File.WriteAllBytes(logPath, [.. bytes, .. new byte[24]]);

        using (var store = new JsonlSessionPersistence(root, warn: _ => { }))
        {
            using var handle = store.Open(header.Id, SessionAccess.Read);
            Assert.Equal(EventCount, handle.Read().Count);
        }

        Assert.False(File.Exists(SessionLogIndex.PathFor(logPath)), "末尾半帧时不该写索引");
    }

    private static void CorruptMiddleFrame(string logPath)
    {
        var bytes = File.ReadAllBytes(logPath);
        var frames = ZstdFrames.Scan(bytes).Frames;
        Assert.True(frames.Count >= 4);
        var victim = frames[1];
        for (var index = victim.Start + 6; index < victim.End - 1; index += 3)
            bytes[index] ^= 0xFF;
        File.WriteAllBytes(logPath, bytes);
    }

    private string WriteLog(string root, SessionHeader header, int eventCount)
    {
        // 这个用例要研究"多帧日志"的索引重建与窗口化, 所以显式把帧预算调小:
        // 写侧按真实明文字节切帧, 用默认 1 MB 预算时这几千条小事件只会落成一帧。
        using (var store = new JsonlSessionPersistence(root, writeBatch: SmallFrames))
        {
            using var handle = store.Create(header);
            for (var offset = 0; offset < eventCount; offset += BatchSize)
            {
                var count = Math.Min(BatchSize, eventCount - offset);
                handle.Append([.. Enumerable.Range(offset, count).Select(NewEvent)]);
            }
        }
        return JsonlLayout.LogPath(root, header.Cwd, header.Id, JsonlCompression.Zstd);
    }

    /**
     * 64 KB 明文的帧预算: 既让这 6,000 条小事件落成十几帧(索引重建与窗口化才有意义),
     * 又不至于小到让窗口读取的 64 KB 最小读量一跨就横跨整份日志。
     */
    private const int FrameBytes = 64 * 1024;

    private static readonly JsonlWriteBatch SmallFrames =
        new(JsonlWriteBatch.DefaultMaxEvents, JsonlWriteBatch.DefaultInterval) { MaxPlaintextBytes = FrameBytes };

    private static SessionEvent NewEvent(int seq) => new()
    {
        Type = SessionEventTypes.StepStart,
        Seq = seq,
        Time = 1_700_000_000_000 + seq,
        Data = new StepStartPayload(1, seq),
    };

    private string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-index-rebuild-{Guid.NewGuid():N}");
        _roots.Add(dir);
        return dir;
    }

    private static SessionHeader NewHeader(string root) => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        CreatedAt = 1_700_000_000_000,
        Cwd = Path.Combine(root, "workspace"),
        IsSeeded = false,
    };

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}