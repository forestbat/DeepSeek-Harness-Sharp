using System.Diagnostics;
using System.Text.Json;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 大响应在客户端的处理成本拆分: 解压、读成 JsonElement、再反序列化成对象。
 * 手机上 3000 条事件光"解析"就占了一秒, 得先量出这三段各占多少, 才知道该优化哪一段。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class SnapshotParseCostTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task BreakDownClientSideCostOfA3000EventPage()
    {
        await using var host = await HarnessHostFixture.CreateAsync("pc", Ct);
        var reader = await host.ConnectAsync("parse-cost", Ct);
        var session = host.Environment.Agent.Session;
        Fill(session, 4000);
        var page = await reader.GetSessionSnapshotRangeAsync(session.Id.Value, 0, 3000, Ct);
        Assert.Equal(3000, page.Events.Count);

        // 还原线上的样子: 整页序列化后按 FrameCodec 写成一帧, 再单独量各段。
        var payload = JsonSerializer.SerializeToUtf8Bytes(page, HostProtocolJsonContext.Default.RemoteSessionSnapshot);
        var wire = await EncodeAsync(payload);

        var rounds = 3;
        var frameMs = 0L;
        var elementMs = 0L;
        var objectMs = 0L;
        for (var round = 0; round < rounds; round += 1)
        {
            var clock = Stopwatch.StartNew();
            var decoded = await DecodeAsync(wire);
            frameMs += clock.ElapsedMilliseconds;

            clock.Restart();
            using var document = JsonDocument.Parse(decoded);
            var element = document.RootElement;
            elementMs += clock.ElapsedMilliseconds;

            clock.Restart();
            var restored = JsonSerializer.Deserialize(element, HostProtocolJsonContext.Default.RemoteSessionSnapshot);
            objectMs += clock.ElapsedMilliseconds;
            Assert.Equal(3000, restored!.Events.Count);
        }
        TestContext.Current.SendDiagnosticMessage(
            $"wire={wire.Length / 1024}KB frame+inflate={frameMs / rounds}ms toElement={elementMs / rounds}ms toObject={objectMs / rounds}ms");
    }

    private static async Task<byte[]> EncodeAsync(byte[] payload)
    {
        using var stream = new MemoryStream();
        await Dsh.Transport.FrameCodec.WriteFrameAsync(stream, payload, Ct);
        return stream.ToArray();
    }

    private static async Task<byte[]> DecodeAsync(byte[] wire)
    {
        using var stream = new MemoryStream(wire);
        return await Dsh.Transport.FrameCodec.ReadFrameAsync(stream, cancellationToken: Ct) ?? [];
    }

    private static void Fill(Dsh.Core.Session session, int count)
    {
        session.Append(new Dsh.Core.TurnStartPayload(1));
        session.Append(new Dsh.Core.StepStartPayload(1, 1));
        var text = new string('x', 400);
        for (var index = 0; index < count; index += 1)
            session.Append(new Dsh.Core.AssistantChunkPayload(1, 1, new Dsh.Llm.StreamChunk.TextDelta(0, text)));
    }
}
