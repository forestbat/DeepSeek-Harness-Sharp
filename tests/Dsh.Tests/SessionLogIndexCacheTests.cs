using System.Text;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 帧索引的进程内缓存: 同一份日志在同一个进程里只解析一次, 文件一变就必须重读。
 *
 * 判据用对象同一性而不只是毫秒数: 一次成功的复用返回的就是同一个实例, 重新解析必然造出新对象。
 */
public sealed class SessionLogIndexCacheTests : IDisposable
{
    private const int EventCount = 6_000;
    private const int BatchSize = 500;
    private const int TailEvents = 1_000;
    private readonly List<string> _roots = [];

    [Fact]
    public void LoadingTheSameLogTwice_ReturnsTheSameInstance()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);

        var first = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(first);
        var second = SessionLogIndex.TryLoad(logPath);

        Assert.Same(first, second);
    }

    [Fact]
    public void WindowedOpenAndTheWriteHandle_ShareOneIndex()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);

        SessionLogIndex? loaded;
        using (var store = new JsonlSessionPersistence(root))
        {
            var window = store.TryOpenWindow(header.Id, TailEvents);
            Assert.NotNull(window);
            loaded = SessionLogIndex.TryLoad(logPath);
            Assert.NotNull(loaded);
            using var write = store.Open(header.Id, SessionAccess.Write);
        }

        // 第二个进程内实例(等价于"再打开一次")拿到的还是同一份索引。
        using (var reopened = new JsonlSessionPersistence(root))
        {
            var window = reopened.TryOpenWindow(header.Id, TailEvents);
            Assert.NotNull(window);
        }
        Assert.Same(loaded, SessionLogIndex.TryLoad(logPath));
    }

    [Fact]
    public void AppendingToTheLog_InvalidatesTheCachedIndex()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        var before = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(before);

        using (var store = new JsonlSessionPersistence(root))
        {
            using var handle = store.Open(header.Id, SessionAccess.Write);
            handle.Append([.. Enumerable.Range(EventCount, 400).Select(NewEvent)]);
            handle.Flush();
        }

        var after = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(after);
        Assert.NotSame(before, after);
        Assert.Equal(new FileInfo(logPath).Length, after.Length);
        Assert.Equal(before.EventCount + 400, after.EventCount);
    }

    [Fact]
    public void LogGrowingUnderTheIndex_IsNotServedFromCache()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        Assert.NotNull(SessionLogIndex.TryLoad(logPath));

        // 日志被别的写入者推进了、索引没跟上: 缓存里那份不再覆盖当前文件, 必须判失效。
        using (var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.Read))
            stream.Write(new byte[16]);

        Assert.Null(SessionLogIndex.TryLoad(logPath));
    }

    [Fact]
    public void DeletedIndex_IsNotServedFromCache()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        Assert.NotNull(SessionLogIndex.TryLoad(logPath));

        SessionLogIndex.Delete(logPath);

        Assert.Null(SessionLogIndex.TryLoad(logPath));
        Assert.False(File.Exists(SessionLogIndex.PathFor(logPath)));
    }

    [Fact]
    public void RewritingTheIndex_IsPickedUpAgain()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        var first = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(first);

        // 索引被删掉(修复路径会这么干): 缓存不能还记着那份已经不存在的索引。
        SessionLogIndex.Delete(logPath);
        Assert.Null(SessionLogIndex.TryLoad(logPath));

        // 重建之后 Save 一遍: 落盘的那份变了, 缓存就该失效, 下一次读盘重新拿到它。
        first.Save(logPath);

        var again = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(again);
        Assert.NotSame(first, again);
        Assert.Equal(first.Frames.Count, again.Frames.Count);
        Assert.Equal(first.Length, again.Length);
    }

    [Fact]
    public void LegacyJsonIndex_IsUpgradedToTheBinaryFormatInPlace()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        var indexPath = SessionLogIndex.PathFor(logPath);
        var expected = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(expected);
        var legacy = ToLegacyJson(expected);
        SessionLogIndex.Delete(logPath);
        File.WriteAllText(indexPath, legacy);

        var upgraded = SessionLogIndex.TryLoad(logPath);

        Assert.NotNull(upgraded);
        Assert.Equal(expected.Frames.Count, upgraded.Frames.Count);
        Assert.Equal(expected.EventCount, upgraded.EventCount);
        Assert.Equal(expected.Length, upgraded.Length);
        for (var position = 0; position < expected.Frames.Count; position += 1)
            Assert.Equal(expected.Frames[position], upgraded.Frames[position]);
        // 读一次之后就落成二进制: 下次不必再解析一遍 JSON。
        var bytes = File.ReadAllBytes(indexPath);
        Assert.Equal("DSHFRAM2"u8.ToArray(), bytes.AsSpan(0, 8).ToArray());
        Assert.Same(upgraded, SessionLogIndex.TryLoad(logPath));
    }

    [Fact]
    public void CorruptBinaryIndex_IsRebuilt_AndTheNextOpenIsWindowed()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        var indexPath = SessionLogIndex.PathFor(logPath);
        Assert.NotNull(SessionLogIndex.TryLoad(logPath));

        // 帧记录写到一半(断电/被杀): 文件里剩下的半个记录不算条目, 末帧不再对齐日志长度。
        var bytes = File.ReadAllBytes(indexPath);
        var truncated = bytes.AsSpan(0, bytes.Length - 7).ToArray();
        SessionLogIndex.Delete(logPath);
        File.WriteAllBytes(indexPath, truncated);

        Assert.Null(SessionLogIndex.TryLoad(logPath));
        long events;
        using (var store = new JsonlSessionPersistence(root, warn: _ => { }))
        {
            using var handle = store.Open(header.Id, SessionAccess.Read);
            events = handle.Read().Count;
        }

        var rebuilt = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(rebuilt);
        Assert.Equal(events, rebuilt.EventCount);
        using (var reopened = new JsonlSessionPersistence(root, warn: _ => { }))
            Assert.NotNull(reopened.TryOpenWindow(header.Id, TailEvents));
    }

    /** 老版本的 JSON 行索引: 头行 + 每帧一行。 */
    private static string ToLegacyJson(SessionLogIndex index)
    {
        var builder = new StringBuilder();
        builder.Append($$"""{"v":1,"events":{{index.EventCount}},"seqBase":{{index.SeqBase}}}""").Append('\n');
        foreach (var frame in index.Frames)
        {
            builder.Append(
                    $$"""{"start":{{frame.Start}},"end":{{frame.End}},"first":{{frame.FirstSeq}},"count":{{frame.EventCount}}}""")
                .Append('\n');
        }
        return builder.ToString();
    }

    private string WriteLog(string root, SessionHeader header, int eventCount)
    {
        using (var store = new JsonlSessionPersistence(root))
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

    private static SessionEvent NewEvent(int seq) => new()
    {
        Type = SessionEventTypes.StepStart,
        Seq = seq,
        Time = 1_700_000_000_000 + seq,
        Data = new StepStartPayload(1, seq),
    };

    private string NewRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"dsh-index-cache-{Guid.NewGuid():N}");
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