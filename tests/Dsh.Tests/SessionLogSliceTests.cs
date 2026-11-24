using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 日志字节切片: 宿主把历史当**字节**交出去, 客户端自己解析。
 *
 * 这条路径的契约有三条, 缺一条就会让"打开会话"要么慢回原样、要么少一段历史:
 * 拼回去的序号必须连续; 段与段之间不能有缺口(帧边界只落在行边界上, 相邻段可能重叠, 但绝不能漏);
 * 从中间开始的一段也能解出正确的绝对序号。
 */
public sealed class SessionLogSliceTests : IDisposable
{
    private const int EventCount = 45_000;
    private readonly List<string> _roots = [];

    [Fact]
    public void WholeHistoryAsSlices_ParsesBackToEveryEventInOrder()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var events = ReadAllSlices(store, header.Id, total, pageEvents: 20_000);
        Assert.Equal(total, events.Count);
        for (var index = 0; index < events.Count; index += 1)
            Assert.Equal(index, events[index].Seq);
    }

    [Fact]
    public void SliceFromTheMiddle_KeepsAbsoluteSequenceNumbers()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slice = store.TryReadLogSlice(header.Id, 30_000, 2_000);
        Assert.NotNull(slice);
        var events = SessionLogSliceParser.Parse(slice.Bytes, slice.FromSeq);
        Assert.NotEmpty(events);
        // 段首是"覆盖请求起点的那一帧"的首序号, 可能早于请求起点(帧边界只落在行边界上); 编号必须是绝对序号。
        Assert.True(slice.FromSeq <= 30_000, $"the slice starts after the requested event: {slice.FromSeq}");
        Assert.Equal(slice.FromSeq, events[0].Seq);
        Assert.Equal(slice.FromSeq + slice.EventCount - 1, events[^1].Seq);
        Assert.Contains(events, sessionEvent => sessionEvent.Seq == 30_000);
        Assert.Equal(total, slice.TotalEventCount);
        // 切片不可变, 内容哈希就是它的标识: 接收方据此校验(与去重/缓存)。同一帧覆盖的两个起点得到同一段字节, 哈希相同。
        Assert.Equal(SessionLogSlice.HashOf(slice.Bytes), slice.ContentHash);
        Assert.NotEqual(slice.ContentHash, store.TryReadLogSlice(header.Id, 0, 2_000)!.ContentHash);
        // 解析出来的条数与索引声明的段长一致(帧边界可能多带, 由调用方按序号裁)。
        Assert.Equal(slice.EventCount, events.Count);
    }

    [Fact]
    public void SliceHeader_MatchesTheStoredHeader()
    {
        var (root, header, _) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slice = store.TryReadLogSlice(header.Id, 0, 1_000);
        Assert.NotNull(slice);
        Assert.Equal(header.Id, slice.Header.Id);
        Assert.Equal(header.Id, SessionLogSliceParser.ReadHeader(slice.Bytes).Id);
    }

    /**
     * 逐批解析与一次性解析必须给出同一串事件: 前者只是把同一批结果拆开交出去(交给投影、消费完即释放),
     * 拆的粒度是一帧一个批次 —— 攒成一批就等于什么都没省。
     */
    [Fact]
    public void BatchedParse_YieldsOneBatchPerFrame_AndTheSameEvents()
    {
        var (root, header, _) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slice = store.TryReadLogSlice(header.Id, 0, 20_000);
        Assert.NotNull(slice);
        var batches = SessionLogSliceParser.ParseBatches(slice.Bytes, slice.FromSeq).ToList();
        var wholesale = SessionLogSliceParser.Parse(slice.Bytes, slice.FromSeq);
        Assert.All(batches, Assert.NotEmpty);
        Assert.Equal(wholesale.Count, batches.Sum(batch => batch.Count));
        // 头帧只带表头, 事件帧每帧交出一批: 批次数量因此必须与事件帧数一致。
        Assert.Equal(Math.Max(1, ZstdFrames.Scan(slice.Bytes).Frames.Count - 1), batches.Count);
        var single = batches.SelectMany(batch => batch).ToList();
        for (var index = 0; index < wholesale.Count; index += 1)
        {
            Assert.Equal(wholesale[index].Seq, single[index].Seq);
            Assert.Equal(wholesale[index].Time, single[index].Time);
            Assert.Equal(wholesale[index].Type, single[index].Type);
        }
    }

    /**
     * 只读信封: 条数、序号、时刻、类型必须与整段解析完全一致, 且一条负载都不建。
     * 这是"从头扫到尾只查表"的前提 —— 信封错了, 扫出来的就是错的位置。
     */
    [Fact]
    public void Envelopes_MatchParsedEventsWithoutMaterializingPayloads()
    {
        var (root, header, total) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slice = store.TryReadLogSlice(header.Id, 0, 20_000);
        Assert.NotNull(slice);
        var parsed = SessionLogSliceParser.Parse(slice.Bytes, slice.FromSeq);
        var envelopes = SessionLogFrameCodec.ReadEnvelopes(slice.Bytes, slice.FromSeq, slice.FromSeq + slice.EventCount);
        Assert.Equal(parsed.Count, envelopes.Count);
        for (var index = 0; index < parsed.Count; index += 1)
        {
            Assert.Equal(parsed[index].Seq, envelopes[index].Seq);
            Assert.Equal(parsed[index].Time, envelopes[index].Time);
            Assert.Equal(parsed[index].Type, envelopes[index].Type);
        }
        // 按位置解出来的那一条必须就是它自己(打包行会带出整块成员, 由实现挑出匹配的那条)。
        var sample = envelopes[envelopes.Count / 2];
        var one = SessionLogFrameCodec.DecodeRecord(slice.Bytes, sample);
        Assert.Contains(one, sessionEvent => sessionEvent.Seq == sample.Seq);
    }

    /** 坏切片上两条入口的失败方式必须一致: 拿不到整段就整段不用, 不给半段历史。 */
    [Fact]
    public void BatchedParse_RejectsTheSameCorruptSlicesAsParse()
    {
        var (root, header, _) = Store();
        using var store = new JsonlSessionPersistence(root);
        var slice = store.TryReadLogSlice(header.Id, 0, 5_000);
        Assert.NotNull(slice);
        var torn = slice.Bytes[..^8];
        Assert.Throws<FormatException>(() => SessionLogSliceParser.Parse(torn, slice.FromSeq));
        Assert.Throws<FormatException>(() => SessionLogSliceParser.ParseBatches(torn, slice.FromSeq).ToList());
        Assert.Throws<FormatException>(() => SessionLogSliceParser.Parse([], 0));
        Assert.Throws<FormatException>(() => SessionLogSliceParser.ParseBatches([], 0).ToList());
    }

    /** 按页取整段(与 GUI 打开时的取法一致), 解析后按序号拼起来。 */
    private static List<SessionEvent> ReadAllSlices(JsonlSessionPersistence store, SessionId id, long total, int pageEvents)
    {
        var events = new List<SessionEvent>();
        long expected = 0;
        for (var from = 0L; from < total; from += pageEvents)
        {
            var slice = store.TryReadLogSlice(id, from, (int)Math.Min(pageEvents, total - from));
            Assert.NotNull(slice);
            foreach (var sessionEvent in SessionLogSliceParser.Parse(slice.Bytes, slice.FromSeq))
            {
                if (sessionEvent.Seq < expected)
                    continue;
                Assert.Equal(expected, sessionEvent.Seq);
                events.Add(sessionEvent);
                expected = sessionEvent.Seq + 1;
            }
        }
        return events;
    }

    private (string Root, SessionHeader Header, long Total) Store()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var events = BuildEvents(header, EventCount);
        using (var writer = new JsonlSessionPersistence(root).Create(header))
            writer.Append(events);
        return (root, header, events.Count);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsh-slice", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    private static SessionHeader NewHeader(string root) => new()
    {
        Id = SessionId.Create(Guid.NewGuid().ToString("N")),
        Version = SessionHeader.SessionFormatVersion,
        Cwd = Path.Combine(root, "workspace"),
        CreatedAt = 1_700_000_000_000,
        IsSeeded = false,
    };

    private static List<SessionEvent> BuildEvents(SessionHeader header, int count)
    {
        var session = Session.Create(header.Id, null, header);
        while (session.Seq < count)
        {
            session.Append(new TurnStartPayload((int)(session.Seq / 3) + 1));
            session.Append(new StepStartPayload((int)(session.Seq / 3) + 1, 1));
            session.Append(new AssistantChunkPayload((int)(session.Seq / 3) + 1, 1,
                new StreamChunk.TextDelta(0, $"第 {session.Seq} 行的正文, 用来把日志切成若干帧。\n")));
        }
        return [.. session.SnapshotEvents()];
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