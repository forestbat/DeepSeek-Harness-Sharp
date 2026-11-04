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

    private static async Task<RemoteSessionSnapshot> SnapshotPageAsync(RemoteHostClient client, string sessionId, long fromEvent, int pageBytes)
    {
        var request = new RemoteSessionSnapshotRequest(sessionId, fromEvent, pageBytes);
        var parameters = JsonSerializer.SerializeToElement(request, HostProtocolJsonContext.Default.RemoteSessionSnapshotRequest);
        var result = await client.RequestAsync(HostProtocol.MethodSessionSnapshot, parameters, Ct);
        return JsonSerializer.Deserialize(result, HostProtocolJsonContext.Default.RemoteSessionSnapshot)!;
    }
}
