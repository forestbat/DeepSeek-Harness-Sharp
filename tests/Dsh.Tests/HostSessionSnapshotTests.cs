using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/** 会话历史必须能整段过线: 历史长度不受单帧上限约束, 也不能因为一帧装不下就扯断连接。 */
[Collection(SerialProcessCollection.Name)]
public sealed class HostSessionSnapshotTests
{
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

    private static async Task<RemoteSessionSnapshot> SnapshotPageAsync(RemoteHostClient client, string sessionId, long fromEvent, int pageBytes)
    {
        var request = new RemoteSessionSnapshotRequest(sessionId, fromEvent, pageBytes);
        var parameters = JsonSerializer.SerializeToElement(request, HostProtocolJsonContext.Default.RemoteSessionSnapshotRequest);
        var result = await client.RequestAsync(HostProtocol.MethodSessionSnapshot, parameters, Ct);
        return JsonSerializer.Deserialize(result, HostProtocolJsonContext.Default.RemoteSessionSnapshot)!;
    }
}
