using System.Text.Json.Nodes;

namespace Dsh.Ptc;

/** NDJSON 帧写入: 一行一个 JSON 对象, 以 \n 结尾; 写入串行化。 */
public sealed class FrameWriter(TextWriter writer)
{
    private readonly Lock _gate = new();

    public void Write(JsonObject frame)
    {
        lock (_gate)
        {
            writer.Write(frame.ToJsonString());
            writer.Write('\n');
            writer.Flush();
        }
    }

    public Task WriteAsync(JsonObject frame)
    {
        Write(frame);
        return Task.CompletedTask;
    }
}

/** NDJSON 帧读取: 跳过空行, 返回一个 JSON 对象或 null（流结束）。 */
public sealed class FrameReader(TextReader reader)
{
    public async Task<JsonObject?> ReadAsync()
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null)
                return null;
            if (line.Length == 0)
                continue;
            return JsonNode.Parse(line) as JsonObject;
        }
    }
}
