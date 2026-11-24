using Dsh.Core;
using Dsh.Gui.Services;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 惰性历史视图: 索引常驻、负载按段解码。
 *
 * 这一层的契约与"整段物化"完全一样 —— 条数、逐条序号、逐条信封与负载都必须对得上,
 * 否则打开的会话要么少一段, 要么指向别的事件。所以这里全部用**真日志**做对照:
 * 同一份日志, 一边是整段解析(SessionLogSliceParser), 一边是按段取(SessionEventLog)。
 */
public sealed class SessionEventLogTests : IDisposable
{
    private const int EventCount = 45_000;
    private readonly List<string> _roots = [];

    [Fact]
    public void LazyLog_MatchesMaterializedEvents()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slices = ReadSlices(store, header.Id, total, pageEvents: 20_000);
        var log = SessionEventLog.TryFromSlices(slices, total);
        Assert.NotNull(log);
        Assert.True(log.IsPaged);
        Assert.Equal(total, log.Count);
        var eager = EagerBySeq(store, header.Id, total);
        Assert.Equal(total, eager.Count);
        for (var index = 0; index < total; index += 1)
        {
            var expected = eager[index];
            var actual = log[index];
            Assert.Equal(expected.Seq, actual.Seq);
            Assert.Equal(expected.Time, actual.Time);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Data, actual.Data);
        }
        // 逐条取过一遍之后仍然是惰性视图: 峰值只与常驻段数有关, 不随"取过多少"增长。
        Assert.True(log.IsPaged);
    }

    /** 前缀释放后下标要整体前移: 折叠边投影边丢前缀靠的就是这个。 */
    [Fact]
    public void RemovePrefix_ShiftsTheWindowWithoutLosingEvents()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var log = SessionEventLog.TryFromSlices(ReadSlices(store, header.Id, total, 20_000), total)!;
        log.RemovePrefix(20_000);
        Assert.Equal(total - 20_000, log.Count);
        Assert.Equal(20_000, log[0].Seq);
        Assert.Equal(total - 1, log[^1].Seq);
        var batch = log.Slice(10, 3);
        Assert.Equal(new long[] { 20_010, 20_011, 20_012 }, batch.Select(sessionEvent => sessionEvent.Seq));
    }

    /**
     * 段头只放掉一部分(投影按回合释放前缀时就是这样): 段内偏移必须跟着挪。
     *
     * 不改起点的话 `this[i]` 会退回已经放掉的那几条, 实时追加的事件于是被当成"已渲染"丢掉 ——
     * 表现是底部停在旧内容不再刷新, 而日志本身完好。
     */
    [Fact]
    public void RemovePartialPrefix_KeepsTheAbsolutePositions()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var log = SessionEventLog.TryFromSlices(ReadSlices(store, header.Id, total, 20_000), total)!;
        var eager = EagerBySeq(store, header.Id, total);

        log.RemovePrefix(3);

        Assert.Equal(total - 3, log.Count);
        Assert.Equal(3, log[0].Seq);
        Assert.Equal(eager[3].Data, log[0].Data);
        Assert.Equal(total - 1, log[^1].Seq);
        Assert.Equal(new long[] { 3, 4, 5 }, log.Slice(0, 3).Select(sessionEvent => sessionEvent.Seq));
    }

    /** 释放过前缀之后再来实时事件: 新的事件必须仍按真实序号定位, 不能被算成"已渲染"。 */
    [Fact]
    public void Append_AfterPartialPrefixRelease_KeepsTheRealSeqs()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var log = SessionEventLog.TryFromSlices(ReadSlices(store, header.Id, total, 20_000), total)!;
        log.RemovePrefix(5);
        var extra = new SessionEvent { Type = "test/extra", Seq = total + 7, Time = 1, Data = new StepStartPayload(1, 2) };

        log.Append([extra]);

        Assert.Equal(total - 5 + 1, log.Count);
        Assert.Equal(total - 1, log[^2].Seq);
        Assert.Equal(total + 7, log[^1].Seq);
        Assert.Same(extra, log[^1]);
    }

    /** 切片覆盖不到整段时照样建索引: 缺口由 Coverage 报出来, 而不是让调用方退回整段物化。 */
    [Fact]
    public void TryFromSlices_ReportsCoverageHolesInsteadOfFailingLazily()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slices = ReadSlices(store, header.Id, total, 20_000);
        Assert.True(slices.Count >= 3);

        // 中间那一段缺失: 索引仍然建起来, 只是分成两段覆盖区间。
        var log = SessionEventLog.TryFromSlices([slices[0], slices[2]], total);
        Assert.NotNull(log);
        Assert.Equal(2, log.Coverage.Count);
        Assert.Equal(new SeqRange(0, slices[0].EventCount), log.Coverage[0]);
        Assert.True(log.Coverage[1].Start > log.Coverage[0].End);
        Assert.False(log.Covers(log.Coverage[0].End));
        Assert.True(log.Covers(log.Coverage[1].Start));
    }

    /** 补洞: 补回来的那一段要插在它自己的位置上, 不是追加到末尾。 */
    [Fact]
    public void Fill_InsertsTheMissingRangeInPlace()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slices = ReadSlices(store, header.Id, total, 20_000);
        var log = SessionEventLog.TryFromSlices([slices[0], slices[2]], total);
        Assert.NotNull(log);
        var hole = new SeqRange(log.Coverage[0].End, log.Coverage[1].Start);
        Assert.True(hole.Count > 0);
        var eager = EagerBySeq(store, header.Id, total);
        var filled = eager.Where(sessionEvent => sessionEvent.Seq >= hole.Start && sessionEvent.Seq < hole.End).ToList();

        log.Fill(filled);

        Assert.Single(log.Coverage);
        Assert.Equal(new SeqRange(0, total), log.Coverage[0]);
        Assert.Equal(total, log.Count);
        for (var index = 0; index < 64; index += 1)
            Assert.Equal(index, log[index].Seq);
        // 补回来的那几条按原位可读。
        Assert.Equal(hole.Start, log[(int)hole.Start].Seq);
        Assert.Equal(eager[(int)hole.Start].Data, log[(int)hole.Start].Data);
    }

    /** 补洞不重复收: 已经覆盖的序号跳过, 不会把条数算成两倍。 */
    [Fact]
    public void Fill_IgnoresEventsAlreadyIndexed()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var log = SessionEventLog.TryFromSlices(ReadSlices(store, header.Id, total, 20_000), total)!;
        var eager = EagerBySeq(store, header.Id, total);

        log.Fill([.. eager.Take(10)]);

        Assert.Equal(total, log.Count);
        Assert.Single(log.Coverage);
    }

    /** 实时追加的尾巴可以接在惰性段后面: 打开的会话随后一直在长。 */
    [Fact]
    public void Append_ExtendsTheIndexedTail()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var log = SessionEventLog.TryFromSlices(ReadSlices(store, header.Id, total, 20_000), total)!;
        var extra = new SessionEvent { Type = "test/extra", Seq = total, Time = 1, Data = new StepStartPayload(1, 2) };
        log.Append([extra]);
        Assert.Equal(total + 1, log.Count);
        Assert.Equal(total, log[^1].Seq);
        Assert.Same(extra, log[^1]);
    }

    /** 撤回: 丢掉序号 >= keep 的事件, 索引与列表长度一起变。 */
    [Fact]
    public void TrimFrom_DropsTheTail()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var log = SessionEventLog.TryFromSlices(ReadSlices(store, header.Id, total, 20_000), total)!;
        log.TrimFrom(30_000);
        Assert.Equal(30_000, log.Count);
        Assert.Equal(29_999, log[^1].Seq);
    }

    /** 一片都没有时没有索引可谈; 少几段则由 Coverage 如实报缺口(见上面的用例)。 */
    [Fact]
    public void TryFromSlices_RejectsAnEmptySliceList()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        Assert.Null(SessionEventLog.TryFromSlices([], total));
    }

    /** 相邻切片多带的那几条(帧边界只落在行边界上)必须裁掉, 否则条数与序号都会重。 */
    [Fact]
    public void TryFromSlices_ClipsTheOverrunOfNeighbouringSlices()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slices = ReadSlices(store, header.Id, total, pageEvents: 100);
        var log = SessionEventLog.TryFromSlices(slices, total);
        Assert.NotNull(log);
        Assert.Equal(total, log.Count);
        for (var index = 0; index < total; index += 997)
            Assert.Equal(index, log[index].Seq);
    }

    private List<RemoteLogSlice> ReadSlices(JsonlSessionPersistence store, SessionId id, long total, int pageEvents)
    {
        var slices = new List<RemoteLogSlice>();
        long expected = 0;
        while (expected < total)
        {
            var slice = store.TryReadLogSlice(id, expected, (int)Math.Min(pageEvents, total - expected));
            Assert.NotNull(slice);
            Assert.Equal(expected, slice.FromSeq);
            // 线缆上交付的就是这个形状: 字节 + 表头 + 段起点 + 段长。
            slices.Add(new RemoteLogSlice(
                slice.Bytes, slice.Header, slice.FromSeq, slice.EventCount, slice.TotalEventCount,
                ContentHash: SessionLogSlice.HashOf(slice.Bytes)));
            expected = slice.FromSeq + slice.EventCount;
        }
        return slices;
    }

    /** 整段物化(老路径)的事件序列, 作为对照。 */
    private static IReadOnlyList<SessionEvent> EagerBySeq(JsonlSessionPersistence store, SessionId id, long total)
    {
        var events = new List<SessionEvent>((int)total);
        long expected = 0;
        while (expected < total)
        {
            var slice = store.TryReadLogSlice(id, expected, (int)Math.Min(20_000, total - expected));
            Assert.NotNull(slice);
            foreach (var sessionEvent in SessionLogSliceParser.Parse(slice.Bytes, slice.FromSeq))
            {
                if (sessionEvent.Seq < expected)
                    continue;
                events.Add(sessionEvent);
                expected = sessionEvent.Seq + 1;
            }
        }
        return events;
    }

    private (string Root, SessionHeader Header, long Total) Store()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-eventlog", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        var header = new SessionHeader
        {
            Id = SessionId.Create(Guid.NewGuid().ToString("N")),
            Version = SessionHeader.SessionFormatVersion,
            Cwd = Path.Combine(root, "workspace"),
            CreatedAt = 1_700_000_000_000,
            IsSeeded = false,
        };
        var session = Session.Create(header.Id, null, header);
        while (session.Seq < EventCount)
        {
            session.Append(new TurnStartPayload((int)(session.Seq / 3) + 1));
            session.Append(new StepStartPayload((int)(session.Seq / 3) + 1, 1));
            session.Append(new AssistantChunkPayload((int)(session.Seq / 3) + 1, 1,
                new StreamChunk.TextDelta(0, $"第 {session.Seq} 行的正文, 用来把日志切成若干帧。\n")));
        }
        var events = session.SnapshotEvents();
        using (var writer = new JsonlSessionPersistence(root).Create(header))
            writer.Append(events);
        return (root, header, events.Count);
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}