using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 事件侧窗口化: 常驻尾窗 + 冻结前缀(按页从持久化读)。
 *
 * 这里只测窗口表示本身 —— 页取得到时与"本来就在内存"等价, 取不到时表现为缺页(null / 抛),
 * 以及截断、追加对窗口的影响。真实日志上的代价见 RealSessionOpenBenchTests。
 */
public sealed class SessionPagingTests
{
    private const int TotalEvents = 10_000;
    private const int ResidentFrom = 8_000;
    /** Session 的页大小: 页边界就是 seq / 4096。 */
    private const int PageSize = 4096;

    [Fact]
    public void ResidentWindowIsServedWithoutTouchingPages()
    {
        var (source, events) = BuildSource(TotalEvents);
        var reads = 0;
        var session = Window(source, events, ResidentFrom, () => reads += 1);

        // 恢复会补一条 seed 收尾事件(与全量恢复一致), 所以总数比日志多一条。
        Assert.Equal(TotalEvents + 1, session.Seq);
        Assert.Equal(ResidentFrom, session.ResidentFrom);
        for (var seq = ResidentFrom; seq < TotalEvents; seq += 7)
            Assert.Equal(seq, session.EventAt(seq)!.Seq);
        Assert.Equal(0, reads);
        Assert.Equal(0, session.PageFaults);
    }

    [Fact]
    public void PrefixSeqFaultsOnePageAndCachesIt()
    {
        var (source, events) = BuildSource(TotalEvents);
        var reads = 0;
        var session = Window(source, events, ResidentFrom, () => reads += 1);

        // 前缀 seq 随机读: 首次缺页, 同页后续命中。
        Assert.Equal(11, session.EventAt(11)!.Seq);
        Assert.Equal(5000, session.EventAt(5000)!.Seq);
        Assert.Equal(12, session.EventAt(12)!.Seq);
        Assert.Equal(5001, session.EventAt(5001)!.Seq);
        Assert.Equal(2, reads);
        Assert.Equal(2, session.PageFaults);
    }

    [Fact]
    public void PageBoundariesAreExactlyAtPageSize()
    {
        var (source, events) = BuildSource(TotalEvents);
        var session = Window(source, events, ResidentFrom, () => { });

        for (var boundary = PageSize; boundary + 1 < ResidentFrom; boundary += PageSize)
        {
            Assert.Equal(boundary - 1, session.EventAt(boundary - 1)!.Seq);
            Assert.Equal(boundary, session.EventAt(boundary)!.Seq);
            Assert.Equal(boundary + 1, session.EventAt(boundary + 1)!.Seq);
        }
        // 尾窗起点的前一条要先缺页读回来, 起点本身就在内存里。
        Assert.Equal(ResidentFrom - 1, session.EventAt(ResidentFrom - 1)!.Seq);
        Assert.Equal(ResidentFrom, session.EventAt(ResidentFrom)!.Seq);
    }

    [Fact]
    public void SnapshotEventsAcrossTheWindowKeepsAbsoluteSeqs()
    {
        var (source, events) = BuildSource(TotalEvents);
        var session = Window(source, events, ResidentFrom, () => { });

        var slice = session.SnapshotEvents(ResidentFrom - 5, ResidentFrom + 5);
        Assert.Equal(10, slice.Count);
        for (var index = 0; index < slice.Count; index += 1)
            Assert.Equal(ResidentFrom - 5 + index, slice[index].Seq);
    }

    [Fact]
    public void UnreadablePageDegradesToNullInsteadOfFabricatingEvents()
    {
        var (source, events) = BuildSource(TotalEvents);
        var session = Session.FromWindow(source.Id,
            new SessionWindow(ResidentFrom, TotalEvents, [.. events.Skip(ResidentFrom)], (_, _) => []),
            source.Header, 0);

        Assert.Null(session.EventAt(123));
        var error = Assert.Throws<SessionPageUnavailableException>(() => session.SnapshotEvents(0, 10)[5]);
        Assert.Equal(5, error.Seq);
        // 尾窗不受影响。
        Assert.Equal(ResidentFrom + 3, session.EventAt(ResidentFrom + 3)!.Seq);
        // 派生消息只看尾窗里的 surface 节点, 不因为前缀缺页而清空。
        Assert.NotEmpty(session.DeriveMessages());
    }

    [Fact]
    public void TruncateBelowTheResidentWindowIsRefused()
    {
        var (source, events) = BuildSource(TotalEvents);
        var session = Window(source, events, ResidentFrom, () => { });

        Assert.False(session.TryTruncateTo(ResidentFrom - 1));
        Assert.True(session.TryTruncateTo(ResidentFrom + 10));
        Assert.Equal(ResidentFrom + 10, session.Seq);
        Assert.Equal(ResidentFrom + 9, session.EventAt(ResidentFrom + 9)!.Seq);
        // 前缀页仍然可读(截断没有往尾窗之前退)。
        Assert.Equal(100, session.EventAt(100)!.Seq);
    }

    [Fact]
    public void AppendAfterWindowContinuesFromTheAbsoluteTotal()
    {
        var (source, events) = BuildSource(TotalEvents);
        var session = Window(source, events, ResidentFrom, () => { });

        var appended = session.Append(new TurnStartPayload(99));
        // 尾窗覆盖 [ResidentFrom, TotalEvents), 恢复时补的 seed 收尾事件占掉 TotalEvents。
        Assert.Equal(TotalEvents + 1, appended.Seq);
        Assert.Equal(TotalEvents + 2, session.Seq);
        Assert.Equal(TotalEvents + 1, session.EventAt(TotalEvents + 1)!.Seq);
    }

    private static Session Window(
        Session source, IReadOnlyList<SessionEvent> events, long residentFrom, Action onRead)
        => Session.FromWindow(
            source.Id,
            new SessionWindow(residentFrom, events.Count, [.. events.Skip((int)residentFrom)],
                (offset, count) =>
                {
                    onRead();
                    var from = (int)offset;
                    if (from >= events.Count)
                        return [];
                    var take = (int)Math.Min(count, events.Count - offset);
                    return [.. events.Skip(from).Take(take)];
                }),
            source.Header,
            0);

    /** 造一份稠密 seq 的事件序列(真实会话就是这么长的: 绝大多数是流式 chunk)。 */
    private static (Session Source, IReadOnlyList<SessionEvent> Events) BuildSource(int count)
    {
        var header = new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = SessionId.Create(Guid.NewGuid().ToString("N")),
            Cwd = "C:/tmp/n4",
            CreatedAt = 0,
            IsSeeded = false,
        };
        var session = Session.Create(header.Id, null, header);
        var turn = 1;
        while (session.Seq < count)
        {
            session.Append(new UserMessagePayload(MessageFactory.CreateUserText($"q{turn}")), new SurfaceOp.Append());
            if (session.Seq >= count) break;
            session.Append(new TurnStartPayload(turn));
            if (session.Seq >= count) break;
            session.Append(new StepStartPayload(turn, 1));
            if (session.Seq >= count) break;
            session.Append(new AssistantChunkPayload(turn, 1, new StreamChunk.TextDelta(0, $"chunk {turn}")));
            if (session.Seq >= count) break;
            session.Append(
                new AssistantMessagePayload(turn, 1,
                    MessageFactory.CreateAssistantMessage([new TextBlock($"a{turn}")], "deepseek-official", "deepseek-v4-flash")),
                new SurfaceOp.Append(),
                [session.Seq - 1]);
            turn += 1;
        }
        return (session, session.SnapshotEvents());
    }
}

