using System.Text.Json;
using Dsh.Core;
using Dsh.Llm;
using Dsh.RemoteHost;
using Dsh.Transport;

namespace Dsh.Tests;

/**
 * 帧层压缩在真实会话上的收益。
 * 手机加载历史慢的根因不是链路带宽, 而是同一份内容以原始 JSON 传输: 落盘 zstd 只有几 MB, 发出去却是几十 MB。
 * 这里走完整协议栈取一段真实历史, 同时统计线上字节, 量化压缩到底省了多少。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class SnapshotCompressionBenefitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SnapshotOverTheWire_IsFarSmallerThanRawJson()
    {
        await using var host = await HarnessHostFixture.CreateAsync("cmp", Ct);
        var reader = await host.ConnectAsync("compression-reader", Ct);
        var session = host.Environment.Agent.Session;
        FillRepetitiveHistory(session, eventCount: 4000);

        var page = await reader.GetSessionSnapshotRangeAsync(session.Id.Value, 0, 3000, Ct);
        Assert.Equal(3000, page.Events.Count);

        // 原始 JSON 大小: 分页预算与测试断言都按这个量算。
        var rawBytes = page.Events.Sum(item =>
            JsonSerializer.SerializeToUtf8Bytes(item, HostProtocolJsonContext.Default.SessionEvent).Length);
        // 线上大小: 整页一条帧, 4 字节头 + 载荷(FrameCodec 决定是否压缩)。
        var payload = JsonSerializer.SerializeToUtf8Bytes(page, HostProtocolJsonContext.Default.RemoteSessionSnapshot);
        var wire = await CompressedFrameSizeAsync(payload);
        var ratio = (double)rawBytes / wire;
        TestContext.Current.SendDiagnosticMessage(
            $"raw={rawBytes / 1024}KB wire={wire / 1024}KB ratio={ratio:F1}x");
        Assert.True(ratio > 3, $"线上只省了 {ratio:F1} 倍, 收益不足");
    }

    /** 按 FrameCodec 的实际行为算线上字节: 够大就走压缩, 压不小才原样。 */
    private static async Task<int> CompressedFrameSizeAsync(byte[] payload)
    {
        using var stream = new MemoryStream();
        await FrameCodec.WriteFrameAsync(stream, payload, Ct);
        return (int)stream.Length;
    }

    /** 接近真实会话的重复度: 相同的字段名与相近的中文内容。 */
    private static void FillRepetitiveHistory(Session session, int eventCount)
    {
        session.Append(new TurnStartPayload(1));
        session.Append(new StepStartPayload(1, 1));
        var text = new string('x', 400);
        for (var index = 0; index < eventCount; index += 1)
            session.Append(new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, text)));
    }
}
