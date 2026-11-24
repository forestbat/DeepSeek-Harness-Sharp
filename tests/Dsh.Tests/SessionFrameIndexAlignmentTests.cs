using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 帧索引与日志必须始终对齐: 帧边界要落在日志真正的帧头上。
 *
 * 曾经的事故: 改名把头部帧换成更长的一帧, 整份日志因此平移, 而索引没跟着平移; 之后照常追加,
 * 追加记录用的是新偏移, 于是"末帧到文件尾"这条结构校验又成立了 —— 错位的边界被当成权威,
 * 窗口读按它解压首页必然失败, 首页永远读不回来(GUI 因此连主窗口都出不来)。
 */
public sealed class SessionFrameIndexAlignmentTests : IDisposable
{
    private const int EventCount = 12_000;
    private const int BatchSize = 500;
    private const int TailEvents = 2_000;
    private const int PageEvents = 8;

    private readonly List<string> _roots = [];

    [Fact]
    public void Rename_KeepsTheFrameIndexAligned_SoThePrefixStillPages()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        WriteLog(root, header, EventCount);

        using (var store = new JsonlSessionPersistence(root))
            store.RenameSession(header, "改名以后标题更长一些的会话");
        // 改名之后继续写: 追加路径会往索引末尾补记录, 于是"日志变长了"再也不会被发现。
        using (var store = new JsonlSessionPersistence(root))
        {
            using var writer = store.Open(header.Id, SessionAccess.Write);
            writer.Append([.. Enumerable.Range(EventCount, 400).Select(NewEvent)]);
            writer.Flush();
        }

        var warnings = new List<string>();
        using var reopened = new JsonlSessionPersistence(root, warn: warnings.Add);
        var window = reopened.TryOpenWindow(header.Id, TailEvents);
        Assert.True(window is not null, $"改名之后窗口化打开被拒: {string.Join("; ", warnings)}");
        var paged = window.Value;
        Assert.True(paged.ResidentFrom > 0);
        Assert.Equal(EventCount + 400, paged.TotalEvents);

        // 前缀(首页)必须还读得回来, 且与全量读一致: 这正是索引错位时失去的能力。
        using var handle = reopened.Open(header.Id, SessionAccess.Read);
        var full = handle.Read();
        var page = paged.ReadPage!(0, PageEvents);
        Assert.True(page.Count > 0, $"首页读不回来: {string.Join("; ", warnings)}");
        for (var index = 0; index < page.Count; index += 1)
            Assert.Equal(full[index].Seq, page[index].Seq);
    }

    /** 结构校验(首帧 0 起、末帧到文件尾、seq 连续)过得去、但帧边界错位的索引: 不许被当成权威。 */
    [Fact]
    public void MisalignedIndexThatStillMatchesTheLength_IsRejectedAndRebuilt()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var logPath = WriteLog(root, header, EventCount);
        var index = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(index);

        // 整份平移 3 字节的等价物: 除首帧外每个起点都往前挪 3 字节。末帧终点不动, 于是长度校验照样通过。
        var misaligned = index.Frames
            .Select(frame => frame.Start == 0 ? frame : frame with { Start = frame.Start - 3 })
            .ToList();
        new SessionLogIndex
        {
            Length = index.Length,
            EventCount = index.EventCount,
            SeqBase = index.SeqBase,
            Frames = misaligned,
        }.Save(logPath);
        Assert.NotNull(SessionLogIndex.TryLoad(logPath));

        var warnings = new List<string>();
        using (var store = new JsonlSessionPersistence(root, warn: warnings.Add))
        {
            Assert.Null(store.TryOpenWindow(header.Id, TailEvents));
            // 窗口化被拒之后必须照旧读得到整份历史(退回全量解析), 而不是报错或读少了。
            using var handle = store.Open(header.Id, SessionAccess.Read);
            Assert.Equal(EventCount, handle.Read().Count);
        }

        // 全量打开顺手重建了索引: 下一次打开重新走窗口化, 首页也读得回来。
        using var reopened = new JsonlSessionPersistence(root);
        var window = reopened.TryOpenWindow(header.Id, TailEvents);
        Assert.True(window is not null, string.Join("; ", warnings));
        var page = window.Value.ReadPage!(0, PageEvents);
        Assert.Equal(PageEvents, page.Count);
        Assert.Equal(0, page[0].Seq);
    }

    /**
     * 第一次启动(磁盘上还没有任何会话)必须照旧可用: 没有日志就没有索引, 也就没有"帧边界"可言,
     * 窗口化打开应当干脆地拒绝, 改名把空会话落成只有头行的日志时同样不该报错。
     */
    [Fact]
    public void SessionWithoutALog_DeclinesWindowedOpen_AndRenamesCleanly()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var warnings = new List<string>();
        using var store = new JsonlSessionPersistence(root, warn: warnings.Add);

        Assert.Null(store.Stat(header.Id));
        Assert.Null(store.TryOpenWindow(header.Id, TailEvents));

        // 空会话改名: 先落成只有头行的日志, 再改写头帧。索引是这一版才落盘的, 平移照常发生。
        store.RenameSession(header, "第一次启动的会话");
        Assert.NotNull(store.Stat(header.Id));
        Assert.Null(store.TryOpenWindow(header.Id, TailEvents));

        using var handle = store.Open(header.Id, SessionAccess.Read);
        Assert.Empty(handle.Read());
        // 全程没有任何"帧边界/页读坏了"的告警: 第一次启动不该撞上索引修复那套东西。
        Assert.DoesNotContain(warnings, warning => warning.Contains("could not be read back", StringComparison.Ordinal));
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-index-alignment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static SessionHeader NewHeader(string root) => new()
    {
        Version = SessionHeader.SessionFormatVersion,
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        CreatedAt = 1_700_000_000_000,
        Cwd = Path.Combine(root, "workspace"),
        IsSeeded = false,
    };

    private string WriteLog(string root, SessionHeader header, int eventCount)
    {
        using (var store = new JsonlSessionPersistence(root))
        {
            using var handle = store.Create(header);
            for (var offset = 0; offset < eventCount; offset += BatchSize)
                handle.Append([.. Enumerable.Range(offset, Math.Min(BatchSize, eventCount - offset)).Select(NewEvent)]);
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

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}