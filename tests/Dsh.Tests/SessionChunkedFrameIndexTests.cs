using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;

namespace Dsh.Tests;

/**
 * 打包记录(chunk 行)跨帧的日志: 帧索引必须能读出来, 字节切片/分页/尾窗必须都能用。
 *
 * 现场形状: 一条 text-chunks 记录是多**行** JSON, 而帧预算按**字节**切, 于是一条记录会被切在两个帧之间 ——
 * 前半帧结束时那条记录还没闭合成一条事件, 差分计数是 0。索引校验原先要求"头帧之后每一帧都必须含事件",
 * 于是这种日志的索引被整份判失效: 每次打开都全量解析(18MB 的会话实测 4.4 秒), 字节切片路径永远返回 null
 * (客户端只能按事件快照取 ~500MB JSON), 而"翻一页"也不再走索引。三条后果都只在大会话上显形。
 */
public sealed class SessionChunkedFrameIndexTests : IDisposable
{
    /** 一帧要装到远超写盘预算, 重切才会把打包记录切在中间。 */
    private const int EventCount = 30_000;

    private readonly List<string> _roots = [];

    /**
     * 重切之后(打包记录被切在帧边界上)索引仍可用, 且字节切片覆盖整份日志、解析结果与全量读逐条一致。
     */
    [Fact]
    public void ReframedPackedLog_KeepsAUsableIndexAndByteSlices()
    {
        var root = NewRoot();
        var header = NewHeader(root);
        var events = BuildEvents(header, EventCount);
        // 造出"一帧装下整段历史"的老形状: 写批次给一个巨大的明文字节预算。默认开启打包,
        // 于是 chunk 会合成一条多行记录 —— 重切时它正好被切在两个帧之间。
        var coarse = new JsonlWriteBatch(JsonlWriteBatch.DefaultMaxEvents, TimeSpan.FromMilliseconds(10))
        {
            MaxPlaintextBytes = 256 * 1024 * 1024,
        };
        using (var writer = new JsonlSessionPersistence(root, writeBatch: coarse).Create(header))
            writer.Append(events);
        var logPath = JsonlLayout.LogPath(root, header.Cwd, header.Id, JsonlCompression.Zstd);

        var warnings = new List<string>();
        using (var store = new JsonlSessionPersistence(root, warn: warnings.Add))
        {
            using var writer = store.Open(header.Id, SessionAccess.Write);
            Assert.Equal(events.Count, writer.Count);
        }
        Assert.Contains(warnings, warning => warning.Contains("re-framed", StringComparison.Ordinal));

        // 1. 索引可读: 一份"跨帧半条记录"的索引不算坏索引。
        //    按修复前写入方的形状再造一份: 把一个帧拆成"半条记录"+"剩下的记录", 前半帧事件数为 0。
        using var reopened = new JsonlSessionPersistence(root);
        var loaded = Assert.IsType<SessionLogIndex>(SessionLogIndex.TryLoad(logPath));
        var split = new List<FrameEntry>(loaded.Frames);
        var target = split[1];
        var half = target.Start + ((target.End - target.Start) / 2);
        split[1] = target with { End = half, EventCount = 0 };
        split.Insert(2, target with { Start = half });
        new SessionLogIndex { Length = loaded.Length, EventCount = loaded.EventCount, SeqBase = loaded.SeqBase, Frames = split }
            .Save(logPath);
        var index = SessionLogIndex.TryLoad(logPath);
        Assert.NotNull(index);
        Assert.Equal(events.Count, index.EventCount);
        // 每一帧都装整数条记录, 并且首尾相接铺满日志。
        for (var slot = 1; slot < index.Frames.Count; slot += 1)
        {
            Assert.True(index.Frames[slot].EventCount > 0, $"frame {slot} holds no complete record");
            Assert.Equal(index.Frames[slot - 1].End, index.Frames[slot].Start);
        }
        Assert.Equal(new FileInfo(logPath).Length, index.Frames[^1].End);

        // 2. 字节切片可用, 而且首片就从 #0 起、解析结果与事件逐条对应。
        var head = reopened.TryReadLogSlice(header.Id, 0, 20_000);
        Assert.NotNull(head);
        Assert.Equal(0, head.FromSeq);
        var parsed = SessionLogSliceParser.Parse(head.Bytes, head.FromSeq);
        Assert.NotEmpty(parsed);
        Assert.Equal(0, parsed[0].Seq);
        for (var position = 0; position < parsed.Count; position += 1)
        {
            Assert.Equal(events[position].Seq, parsed[position].Seq);
            Assert.Equal(events[position].Data.GetType(), parsed[position].Data.GetType());
        }

        // 3. 中段切片同样可读(切片按帧边界对齐, 起点可能比请求的早, 但一条都不能少)。
        var middle = Assert.IsType<SessionLogSlice>(reopened.TryReadLogSlice(header.Id, 12_000, 4_000));
        var middleEvents = SessionLogSliceParser.Parse(middle.Bytes, middle.FromSeq);
        Assert.True(middleEvents[0].Seq <= 12_000, $"middle slice starts at #{middleEvents[0].Seq}, after the request");
        Assert.True(middleEvents[^1].Seq >= 15_999, $"middle slice ends at #{middleEvents[^1].Seq}, before the request");
        for (var position = 1; position < middleEvents.Count; position += 1)
            Assert.Equal(middleEvents[position - 1].Seq + 1, middleEvents[position].Seq);

        // 4. 尾窗走的也是同一份帧边界: 常驻事件必须从 ResidentFrom 连续到日志末尾。
        var window = Assert.IsType<SessionWindow>(reopened.TryOpenWindow(header.Id, 20_000));
        Assert.Equal(events.Count, window.TotalEvents);
        Assert.Equal(window.ResidentFrom, window.Resident[0].Seq);
        Assert.Equal(events.Count - 1, window.Resident[^1].Seq);
        Assert.Equal(events.Count, window.ResidentFrom + window.Resident.Count);
    }

    /** 0 事件帧的合并规则: 中间的并到前一帧, 开头的并到后一帧, 头帧不参与。 */
    [Fact]
    public void Coalesce_AttachesEmptyFramesToTheNeighbourThatCompletesTheRecord()
    {
        var header = new FrameEntry(0, 100, 0, 0);
        var first = new FrameEntry(100, 200, 0, 2) { MinReplaceStart = 40 };
        var continuation = new FrameEntry(200, 260, 2, 0) { MinReplaceStart = 41 };
        var second = new FrameEntry(260, 400, 2, 1);
        var merged = SessionLogIndex.Coalesce([header, first, continuation, second]);
        Assert.Equal(3, merged.Count);
        Assert.Equal(first with { End = 260, MinReplaceStart = 40 }, merged[1]);
        Assert.Equal(second, merged[2]);

        // 开头的 0 事件帧: 并到后一帧, 让"从 0 起一页"仍解得出完整记录。
        var leading = new FrameEntry(100, 180, 0, 0);
        var completed = new FrameEntry(180, 300, 0, 3) { MinReplaceStart = 7 };
        var mergedLeading = SessionLogIndex.Coalesce([header, leading, completed]);
        Assert.Equal(2, mergedLeading.Count);
        Assert.Equal(completed with { Start = 100 }, mergedLeading[1]);

        // 全都 0 事件(极端形状): 合并成一帧, 起点不变, 不丢字节。
        var mergedAllEmpty = SessionLogIndex.Coalesce([header, leading, new FrameEntry(180, 300, 0, 0)]);
        Assert.Equal(2, mergedAllEmpty.Count);
        Assert.Equal((100L, 300L, 0), (mergedAllEmpty[1].Start, mergedAllEmpty[1].End, mergedAllEmpty[1].EventCount));
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-chunked-index-{Guid.NewGuid():N}");
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

    /** 大量 chunk(默认打包成多行记录) + 每 40 回合一条请求头: 与现场日志的比例一致。 */
    private static List<SessionEvent> BuildEvents(SessionHeader header, int count)
    {
        var session = Session.Create(header.Id, null, header);
        var turn = 1;
        while (session.Seq < count)
        {
            if (turn % 40 == 1)
            {
                session.Append(new RequestHeaderPayload(
                    new EpochHeader(new LlmCallConfig("deepseek-official", "deepseek-v4-flash")),
                    RequestHeaderReasons.Initial));
                if (session.Seq >= count) break;
                session.Append(new RequestContextPayload("deepseek-official", "deepseek-v4-flash"));
                if (session.Seq >= count) break;
            }
            session.Append(new TurnStartPayload(turn));
            if (session.Seq >= count) break;
            session.Append(new StepStartPayload(turn, 1));
            if (session.Seq >= count) break;
            session.Append(new AssistantChunkPayload(turn, 1, new StreamChunk.TextDelta(0, $"chunk {session.Seq} 行内容\n")));
            if (session.Seq >= count) break;
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"q{turn}")), new SurfaceOp.Append());
            if (session.Seq >= count) break;
            session.Append(
                new AssistantMessagePayload(turn, 1,
                    MessageFactory.CreateAssistantMessage([new TextBlock($"a{turn}")], "deepseek-official", "deepseek-v4-flash")),
                new SurfaceOp.Append(),
                [session.Seq - 2]);
            turn += 1;
        }
        return [.. session.SnapshotEvents()];
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}