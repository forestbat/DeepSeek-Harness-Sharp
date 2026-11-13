using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 会话历史必须能整段过线: 历史长度不受单帧上限约束, 也不能因为一帧装不下就扯断连接。 */
[Collection(SerialProcessCollection.Name)]
public sealed class HostSessionSnapshotTests
{
    /**
     * 手机只取尾部: 历史再长, 下载到的事件数也不超过请求的窗口。
     * 以前调用方一律拿整段, 几千条事件在手机上直接撑爆进程 —— 这条守住"只取尾部"的契约。
     */
    [Fact]
    public async Task SnapshotTail_DownloadsOnlyTheWindowEvenWhenHistoryIsMuchLonger()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hst", Ct);
        var reader = await host.ConnectAsync("tail-reader", Ct);
        var session = host.Environment.Agent.Session;
        var expected = FillOversizedHistory(session);
        const int window = 120;

        var snapshot = await reader.GetSessionSnapshotAsync(session.Id.Value, window, Ct);

        Assert.Equal(window, snapshot.Events.Count);
        Assert.Equal(expected, snapshot.Events[^1].Seq + 1);
        // 尾部之外的事件一条都不该出现在内存里。
        Assert.Equal(expected - window, snapshot.Events[0].Seq);
    }

    /** 历史比窗口还短时, 尾部窗口就是整段, 不能凭空补空条目。 */
    [Fact]
    public async Task SnapshotTail_ReturnsEverythingWhenHistoryIsShorterThanTheWindow()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hss", Ct);
        var reader = await host.ConnectAsync("short-runner", Ct);
        var session = host.Environment.Agent.Session;
        session.Append(new TurnStartPayload(1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("只有一条")), new SurfaceOp.Append());

        var snapshot = await reader.GetSessionSnapshotAsync(session.Id.Value, tailCount: 500, Ct);

        Assert.Equal(snapshot.Events.Count, (int)session.Seq);
        Assert.Equal(0, snapshot.Events[0].Seq);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /** 一段远超单页预算的历史。 */
    private static int FillOversizedHistory(Session session)
    {
        var delta = new string('x', 16 * 1024);
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        for (var index = 0; index < 1400; index += 1)
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, delta)));
        return (int)session.Seq;
    }

    [Fact]
    public async Task SnapshotOfHistoryLargerThanOneFrame_ReturnsEveryEventAndKeepsTheLink()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsn", Ct);
        var reader = await host.ConnectAsync("snapshot-reader", Ct);
        var session = host.Environment.Agent.Session;
        var expected = FillOversizedHistory(session);

        var snapshot = await reader.GetSessionSnapshotAsync(session.Id.Value, Ct);

        Assert.Equal(expected, snapshot.TotalEventCount);
        Assert.Equal(expected, snapshot.Events.Count);
        Assert.Equal(expected, snapshot.Events[^1].Seq + 1);
        // 一帧超限不能顺手把整条连接带走: 后续请求还得能用。
        await reader.PingAsync(Ct);
    }

    /** 单页受请求里的字节预算约束, 响应带上整段条数, 下一页从上一页末尾续上。 */
    [Fact]
    public async Task SnapshotPage_StopsAtTheByteBudgetAndReportsTheWholeExtent()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsp", Ct);
        var reader = await host.ConnectAsync("page-reader", Ct);
        var session = host.Environment.Agent.Session;
        var expected = FillOversizedHistory(session);
        const int pageBytes = 256 * 1024;

        var first = await SnapshotPageAsync(reader, session.Id.Value, fromEvent: 0, pageBytes);
        var second = await SnapshotPageAsync(reader, session.Id.Value, fromEvent: first.Events.Count, pageBytes);

        Assert.Equal(expected, first.TotalEventCount);
        Assert.InRange(first.Events.Count, 1, expected - 1);
        Assert.Equal(first.Events.Count, second.Events[0].Seq);
        Assert.Equal(expected, second.TotalEventCount);
    }

    /**
     * 分页用的字节估算必须与实际编码一致, 且一页真的不超预算。
     * 估算与实际不符有两种坏结果: 估小了整帧超出传输上限(连接会被带走), 估大了每页装得太少(分页变慢)。
     */
    [Fact]
    public async Task SnapshotPage_NeverExceedsItsByteBudget()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsb", Ct);
        var reader = await host.ConnectAsync("budget-reader", Ct);
        var session = host.Environment.Agent.Session;
        var expected = FillOversizedHistory(session);
        const int pageBytes = 256 * 1024;

        var from = 0L;
        var pages = 0;
        while (from < expected)
        {
            var page = await SnapshotPageAsync(reader, session.Id.Value, from, pageBytes);
            Assert.NotEmpty(page.Events);
            var encoded = page.Events.Sum(item =>
                JsonSerializer.SerializeToUtf8Bytes(item, HostProtocolJsonContext.Default.SessionEvent).Length);
            // 单条就超预算时仍然要发出(否则这条永远取不到), 之外的页都必须装得下。
            Assert.True(page.Events.Count == 1 || encoded <= pageBytes,
                $"第 {pages} 页编码后 {encoded} 字节, 超过预算 {pageBytes}");
            from += page.Events.Count;
            pages += 1;
            Assert.True(pages < 10_000, "分页没有推进");
        }
        Assert.Equal(expected, from);
    }

    /**
     * 分页的字节估算必须与"真的序列化出来有多少字节"逐个事件相等。
     * 估算走的是"写进计数器"的捷径(不为每条事件分配 byte[]); 一旦估算与实际编码不一致,
     * 分页要么切早(多跑几趟往返), 要么切晚(整帧超过传输上限, 连接被带走)。
     */
    [Fact]
    public async Task SnapshotPage_BudgetMatchesActualEncodingPerEvent()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hse", Ct);
        var session = host.Environment.Agent.Session;
        // 覆盖中文与 emoji: 编码器是否转义非 ASCII 会直接改变字节数。
        session.Append(new TurnStartPayload(1));
        session.Append(new UserMessagePayload(MessageFactory.CreateUserText("中文标题 with emoji 🚀")), new SurfaceOp.Append());
        for (var index = 0; index < 5; index += 1)
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, new string('x', 100))));
        var reader = await host.ConnectAsync("encoding-reader", Ct);

        // 一次只允许装下"恰好一条"的预算: 估算若比实际小, 就会多装一条而使页超预算。
        var events = session.SnapshotEvents();
        foreach (var target in events.Take(3))
        {
            var size = JsonSerializer.SerializeToUtf8Bytes(target, HostProtocolJsonContext.Default.SessionEvent).Length;
            var page = await SnapshotPageAsync(reader, session.Id.Value, target.Seq, size);
            Assert.Single(page.Events);
            Assert.Equal(target.Seq, page.Events[0].Seq);
            var next = await SnapshotPageAsync(reader, session.Id.Value, target.Seq, size - 1);
            Assert.Single(next.Events); // 单条超预算也要发, 否则这条永远取不到
        }
    }

    /**
     * 从快照末尾之后订阅, 只该收到之后的新事件, 不能把整段历史再回放一遍。
     * 手机以前从 0 订阅, 17MB 的会话就是几十万条事件挤在同一条流里 —— 界面卡死与"一直下滑到不了底"都来自这里。
     */
    [Fact]
    public async Task SubscribeAfterTailSnapshot_ReplaysNewEventsOnly()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsr", Ct);
        var reader = await host.ConnectAsync("tail-subscriber", Ct);
        var session = host.Environment.Agent.Session;
        FillOversizedHistory(session);

        var snapshot = await reader.GetSessionSnapshotAsync(session.Id.Value, tailCount: 100, Ct);
        var fromSeq = snapshot.Events[^1].Seq + 1;

        // 只发一条新事件, 订阅就必须只回放这一条。
        session.Append(new TurnStartPayload(2));
        var expectedSeq = session.Seq - 1;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var stream = reader.SubscribeAsync(session.Id.Value, fromSeq, stop.Token).GetAsyncEnumerator(stop.Token);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(expectedSeq, stream.Current.Event?.Seq);
        Assert.Equal(fromSeq, snapshot.Events[^1].Seq + 1);
    }

    private static async Task<RemoteSessionSnapshot> SnapshotPageAsync(RemoteHostClient client, string sessionId, long fromEvent, int pageBytes)
    {
        var request = new RemoteSessionSnapshotRequest(sessionId, fromEvent, pageBytes);
        var parameters = JsonSerializer.SerializeToElement(request, HostProtocolJsonContext.Default.RemoteSessionSnapshotRequest);
        var result = await client.RequestAsync(HostProtocol.MethodSessionSnapshot, parameters, Ct);
        return JsonSerializer.Deserialize(result, HostProtocolJsonContext.Default.RemoteSessionSnapshot)!;
    }

    /**
     * 上滑取更早的消息要在一屏之内返回, 否则用户每次滑到头都要等。
     * 这里把一次增量取数的耗时和"取 100 条到底花了多长时间"钉住, 便于定位是服务端重建历史慢还是网络慢。
     */
    [Fact]
    public async Task SnapshotChunk_FetchingAnOlderChunkStaysFastOnALongHistory()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsf", Ct);
        var reader = await host.ConnectAsync("chunk-reader", Ct);
        var session = host.Environment.Agent.Session;
        FillOversizedHistory(session);
        var total = (int)session.Seq;

        // 连取三次同一位置: 服务端若每次都重建整段历史, 三次都会一样慢, 与"页大小"无关。
        var costs = new List<long>();
        for (var round = 0; round < 3; round += 1)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var page = await reader.GetSessionSnapshotFromAsync(session.Id.Value, total / 2, 512 * 1024, Ct);
            clock.Stop();
            costs.Add(clock.ElapsedMilliseconds);
            Assert.NotEmpty(page.Events);
        }
        TestContext.Current.SendDiagnosticMessage($"single page fetch (512KB) x3: {string.Join(", ", costs)}ms total={total}");

        Assert.True(costs.Max() < 500, $"单次取一页耗时 {string.Join(", ", costs)}ms, 超过一屏内返回的预算");
    }

    /**
     * 手机按条数要历史: 只要 maxEvents 条, 不要"装满字节预算"的那一大截。
     * 只按字节切时一页能装上千条事件, 传回来立刻被丢掉, 手机每次上滑都在白等这段时间。
     */
    [Fact]
    public async Task SnapshotPage_HonorsAnEventCountLimitInsteadOfFillingTheByteBudget()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsc", Ct);
        var reader = await host.ConnectAsync("count-reader", Ct);
        var session = host.Environment.Agent.Session;
        FillOversizedHistory(session);
        var total = (int)session.Seq;

        var page = await reader.GetSessionSnapshotRangeAsync(session.Id.Value, total / 2, maxEvents: 100, Ct);

        Assert.Equal(100, page.Events.Count);
        // 总数仍是整段历史, 客户端据此判断后面还有没有。
        Assert.Equal(total, page.TotalEventCount);
    }

    /**
     * 手机一次取三千条(约一屏多的历史)时的服务端成本与真实字节数。
     * 只有把"服务端算多久"和"线上传多久"分开, 才知道慢该往哪边修;
     * 这里用接近真实会话的事件尺寸(几百字节), 而不是压测用的十六 KB 大块。
     */
    [Fact]
    public async Task SnapshotChunk_MeasuresWhatAThreeThousandEventPageCosts()
    {
        await using var host = await HarnessHostFixture.CreateAsync("hsm", Ct);
        var reader = await host.ConnectAsync("measure-reader", Ct);
        var session = host.Environment.Agent.Session;
        FillRealisticHistory(session, eventCount: 6000);
        var total = (int)session.Seq;

        var costs = new List<string>();
        var bytes = 0;
        for (var round = 0; round < 3; round += 1)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var page = await reader.GetSessionSnapshotRangeAsync(session.Id.Value, total / 2, maxEvents: 3000, Ct);
            costs.Add($"{clock.ElapsedMilliseconds}ms");
            bytes = page.Events.Sum(item =>
                JsonSerializer.SerializeToUtf8Bytes(item, HostProtocolJsonContext.Default.SessionEvent).Length);
            Assert.Equal(3000, page.Events.Count);
        }
        TestContext.Current.SendDiagnosticMessage(
            $"3000 events: {string.Join(", ", costs)} wire={bytes / 1024}KB");
    }

    /** 尺寸接近真实会话的事件流: 每条几百字节。 */
    private static void FillRealisticHistory(Session session, int eventCount)
    {
        var text = new string('x', 400);
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        for (var index = 0; index < eventCount; index += 1)
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, text)));
    }
}
