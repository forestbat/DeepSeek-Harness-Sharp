using System.Diagnostics;
using System.Text.Json;
using Dsh.Core;
using Dsh.RemoteHost;

namespace Dsh.Tests;

/**
 * 3000 条事件的反序列化成本拆解。
 * 会话事件的转换器对每条事件都做一次 JsonDocument.ParseValue, 再遍历属性校验外层字段, 之后还要再反序列化 data;
 * 手机上这段就是"解析"耗时的大头, 得先量出各步占比, 才知道改哪里最值。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class SessionEventParseCostTests
{
    [Fact]
    public void BreakDownPerEventDeserializationCost()
    {
        const int count = 3000;
        var payload = BuildPayload(count);
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new RemoteSessionSnapshot(default!, BuildEvents(count), count, null),
            HostProtocolJsonContext.Default.RemoteSessionSnapshot);

        // 1) 整页反序列化(实际客户端走的路径)
        var toSnapshot = new List<long>();
        for (var round = 0; round < 3; round += 1)
        {
            var clock = Stopwatch.StartNew();
            var snapshot = JsonSerializer.Deserialize(json, HostProtocolJsonContext.Default.RemoteSessionSnapshot);
            toSnapshot.Add(clock.ElapsedMilliseconds);
            Assert.Equal(count, snapshot!.Events.Count);
        }

        // 2) 只做 JsonDocument.ParseValue + 外层属性校验, 不构造事件对象
        var envelopeMs = MeasureEnvelopeOnly(payload);

        TestContext.Current.SendDiagnosticMessage(
            $"events={count} json={json.Length / 1024}KB toSnapshot={string.Join("/", toSnapshot)}ms envelopeOnly={envelopeMs}ms");
    }

    /** 复刻转换器里的外层扫描: 这是每条事件都付一次的那部分。 */
    private static long MeasureEnvelopeOnly(string payload)
    {
        var clock = Stopwatch.StartNew();
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(payload));
        var seen = 0;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                continue;
            using var doc = JsonDocument.ParseValue(ref reader);
            _ = doc.RootElement.GetProperty("type").GetString();
            seen += 1;
        }
        clock.Stop();
        return clock.ElapsedMilliseconds;
    }

    private static string BuildPayload(int count)
    {
        var builder = new System.Text.StringBuilder(count * 120);
        for (var index = 0; index < count; index += 1)
            builder.Append("{\"type\":\"assistant/chunk\",\"seq\":").Append(index)
                .Append(",\"time\":1700000000000,\"data\":{\"turn\":1,\"step\":1,\"chunk\":{\"kind\":\"text\",\"text\":\"")
                .Append(new string('x', 200))
                .Append("\"}}},");
        return "[" + builder.ToString().TrimEnd(',') + "]";
    }

    private static List<SessionEvent> BuildEvents(int count)
    {
        var events = new List<SessionEvent>(count);
        for (var index = 0; index < count; index += 1)
            events.Add(new SessionEvent
            {
                Type = SessionEventTypes.AssistantChunk,
                Seq = index,
                Time = 1_700_000_000_000,
                Data = new AssistantChunkPayload(1, 1, new Dsh.Llm.StreamChunk.TextDelta(0, new string('x', 200))),
            });
        return events;
    }
}
