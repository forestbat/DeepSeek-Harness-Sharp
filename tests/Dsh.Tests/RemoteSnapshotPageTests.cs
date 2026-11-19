using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 会话历史分页的起点换算: 请求里的 FromEvent 是**绝对序号**, 而窗口化快照的 Events 只从 ResidentFrom 开始。
 * 把绝对序号直接当下标会切出一页空的, 客户端于是从 0 重新订阅, 整份日志又被读回一遍。
 */
public sealed class RemoteSnapshotPageTests
{
    private const long WindowFrom = 1_000;
    private const long TotalEvents = 1_120;

    [Fact]
    public void Slice_StartsAtTheRequestedAbsoluteSeqInsideAWindowedSnapshot()
    {
        var snapshot = Windowed();

        var page = RemoteSnapshotPage.Slice(snapshot, WindowFrom + 100, HostProtocol.SnapshotPageBytes);

        Assert.NotEmpty(page.Events);
        Assert.Equal(WindowFrom + 100, page.Events[0].Seq);
        Assert.Equal(TotalEvents, page.TotalEventCount);
    }

    [Fact]
    public void Slice_ClampsAPositionBeforeTheWindowToItsFirstEvent()
    {
        var snapshot = Windowed();

        var page = RemoteSnapshotPage.Slice(snapshot, 0, HostProtocol.SnapshotPageBytes);

        Assert.NotEmpty(page.Events);
        Assert.Equal(WindowFrom, page.Events[0].Seq);
    }

    /** 一份"只常驻尾部"的快照: 事件序号从 WindowFrom 起连续到 TotalEvents - 1。 */
    private static RemoteSessionSnapshot Windowed()
    {
        var events = new List<SessionEvent>();
        for (var seq = WindowFrom; seq < TotalEvents; seq += 1)
        {
            events.Add(new SessionEvent
            {
                Type = SessionEventTypes.SessionEndSeed,
                Seq = seq,
                Time = seq,
                Data = new SessionEndSeedPayload(),
            });
        }
        var header = new SessionHeader
        {
            Version = SessionHeader.SessionFormatVersion,
            Id = SessionId.Create("windowed-snapshot-page"),
            CreatedAt = 0,
            IsSeeded = false,
        };
        return new RemoteSessionSnapshot(header, events, TotalEvents);
    }
}