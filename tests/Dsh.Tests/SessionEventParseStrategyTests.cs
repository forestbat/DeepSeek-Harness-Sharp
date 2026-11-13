using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Tests;

/**
 * 会话事件解析路径的三种走法对比, 用来决定优化方向而不是靠猜:
 * A 现状(JsonElement 中转)、B 换成 JsonNode、C 直接把 reader 交给反序列化。
 * 关键是 data 目前在 A 里被解析两遍(ParseValue 建文档, 再从文档构造对象), C 只解析一遍。
 */
[Collection(SerialProcessCollection.Name)]
public sealed class SessionEventParseStrategyTests
{
    private const int EventCount = 3000;

    /** 独立选项: 测试进程没有注册插件的源生成上下文, 用反射型解析器即可比较相对成本。 */
    private static readonly JsonSerializerOptions BindOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public void CompareElementNodeAndReaderStrategies()
    {
        var json = BuildSnapshotJson(EventCount);
        const int rounds = 5;

        // A: 现状。整个快照走源生成上下文, 内含每条事件的转换器。
        var a = new List<long>();
        for (var round = 0; round < rounds; round += 1)
        {
            var clock = Stopwatch.StartNew();
            var snapshot = JsonSerializer.Deserialize(json, Dsh.RemoteHost.HostProtocolJsonContext.Default.RemoteSessionSnapshot);
            a.Add(clock.ElapsedMilliseconds);
            Assert.Equal(EventCount, snapshot!.Events.Count);
        }

        // B: 换成 JsonNode 中转再构造对象。
        var b = new List<long>();
        for (var round = 0; round < rounds; round += 1)
        {
            var clock = Stopwatch.StartNew();
            var node = JsonNode.Parse(json)!;
            var array = (node as JsonObject)?.First(property => property.Value is JsonArray).Value!.AsArray()
                ?? throw new InvalidOperationException($"snapshot json has no array: {System.Text.Encoding.UTF8.GetString(json, 0, Math.Min(120, json.Length))}");
            foreach (var item in array)
                _ = item!.Deserialize<SessionEvent>(BindOptions);
            b.Add(clock.ElapsedMilliseconds);
        }

        // C: 只把 reader 直接交给 payload 反序列化(不构造整个事件对象), 这是"少解析一遍"的理论下限。
        var c = new List<long>();
        for (var round = 0; round < rounds; round += 1)
        {
            var clock = Stopwatch.StartNew();
            var payloads = ReadPayloadsThroughReader(json);
            c.Add(clock.ElapsedMilliseconds);
            Assert.Equal(EventCount, payloads);
        }

        TestContext.Current.SendDiagnosticMessage(
            $"events={EventCount} snapshotJson={json.Length / 1024}KB eventsJson={EventsJsonBytes(json) / 1024}KB "
            + $"A(full object)={Median(a)}ms B(node)={Median(b)}ms C(data only)={Median(c)}ms");
    }

    /** 事件数组序列化后的字节数: 标出这些毫秒对应多少数据。 */
    private static int EventsJsonBytes(byte[] json)
    {
        using var document = JsonDocument.Parse(json);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Array)
                return System.Text.Encoding.UTF8.GetByteCount(property.Value.GetRawText());
        }
        throw new InvalidOperationException(
            $"snapshot json has no array: {System.Text.Encoding.UTF8.GetString(json, 0, Math.Min(160, json.Length))}");
    }

    /** 复刻"单遍"走法: 定位到 data 后直接反序列化, 不建中间文档。 */
    private static int ReadPayloadsThroughReader(byte[] json)
    {
        var reader = new Utf8JsonReader(json);
        var count = 0;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("data"))
                continue;
            reader.Read();
            _ = JsonSerializer.Deserialize<AssistantChunkPayload>(ref reader, BindOptions)!;
            count += 1;
        }
        return count;
    }

    private static long Median(List<long> values)
    {
        var sorted = values.OrderBy(item => item).ToList();
        return sorted[sorted.Count / 2];
    }

    private static byte[] BuildSnapshotJson(int count)
    {
        var events = new List<SessionEvent>(count);
        for (var index = 0; index < count; index += 1)
            events.Add(new SessionEvent
            {
                Type = SessionEventTypes.AssistantChunk,
                Seq = index,
                Time = 1_700_000_000_000,
                Data = new AssistantChunkPayload(1, 1, new StreamChunk.TextDelta(0, new string('x', 400))),
            });
        return JsonSerializer.SerializeToUtf8Bytes(
            new Dsh.RemoteHost.RemoteSessionSnapshot(default!, events, count, null),
            Dsh.RemoteHost.HostProtocolJsonContext.Default.RemoteSessionSnapshot);
    }
}
