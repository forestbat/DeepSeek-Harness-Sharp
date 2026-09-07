using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Dsh.Llm.Anthropic;

internal static class AnthropicSse
{
    public static async IAsyncEnumerable<StreamChunk> Translate(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var eventName = "";
        var data = new StringBuilder();
        var openBlocks = new Dictionary<int, OpenBlock>();

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new LlmException(new LlmFailure(
                    "Anthropic SSE stream ended without message_stop",
                    LlmFailureCodes.StreamClosed));
            }

            if (line.Length == 0)
            {
                if (data.Length == 0)
                    continue;

                var payload = data.ToString();
                data.Clear();
                var root = ParseFrame(payload);

                foreach (var chunk in ProcessFrame(eventName, root, openBlocks))
                    yield return chunk;

                var isStop = root.TryGetProperty("type", out var type)
                             && type.GetString() == "message_stop";
                eventName = "";
                if (isStop)
                    yield break;
                continue;
            }

            if (line[0] == ':')
                continue;

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line["data:".Length..];
                if (value.StartsWith(' '))
                    value = value[1..];
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(value);
            }
        }
    }

    private static JsonElement ParseFrame(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new LlmException(new LlmFailure(
                $"malformed Anthropic SSE payload: {payload[..Math.Min(payload.Length, 120)]}",
                "MALFORMED_RESPONSE"));
        }
    }

    private static IEnumerable<StreamChunk> ProcessFrame(
        string eventName,
        JsonElement frame,
        Dictionary<int, OpenBlock> openBlocks)
    {
        var type = frame.TryGetProperty("type", out var typeProperty)
            ? typeProperty.GetString()
            : eventName;

        switch (type)
        {
            case "content_block_start":
            {
                var index = frame.GetProperty("index").GetInt32();
                var contentBlock = frame.GetProperty("content_block");
                var blockType = contentBlock.GetProperty("type").GetString();
                switch (blockType)
                {
                    case "text":
                        openBlocks[index] = new OpenBlock { Type = "text" };
                        yield return new StreamChunk.BlockStart(index, "text");
                        break;
                    case "thinking":
                        openBlocks[index] = new OpenBlock { Type = "thinking" };
                        yield return new StreamChunk.BlockStart(index, "reasoning");
                        break;
                    case "tool_use":
                        openBlocks[index] = new OpenBlock
                        {
                            Type = "tool-call",
                            Id = contentBlock.TryGetProperty("id", out var id) ? id.GetString() : null,
                            Name = contentBlock.TryGetProperty("name", out var name) ? name.GetString() : null,
                        };
                        yield return new StreamChunk.BlockStart(index, "tool-call");
                        break;
                }

                break;
            }
            case "content_block_delta":
            {
                var index = frame.GetProperty("index").GetInt32();
                var delta = frame.GetProperty("delta");
                var deltaType = delta.GetProperty("type").GetString();
                switch (deltaType)
                {
                    case "text_delta":
                    {
                        var text = delta.GetProperty("text").GetString() ?? "";
                        EnsureOpen(openBlocks, index, "text").Text += text;
                        yield return new StreamChunk.TextDelta(index, text);
                        break;
                    }
                    case "thinking_delta":
                    {
                        var text = delta.GetProperty("thinking").GetString() ?? "";
                        EnsureOpen(openBlocks, index, "thinking").Text += text;
                        yield return new StreamChunk.ReasoningDelta(index, text);
                        break;
                    }
                    case "input_json_delta":
                    {
                        var partial = delta.GetProperty("partial_json").GetString() ?? "";
                        var open = EnsureOpen(openBlocks, index, "tool-call");
                        open.Arguments += partial;
                        yield return new StreamChunk.ToolCallDelta(
                            index,
                            ToolCallId.Create(open.Id ?? $"call-{index}"),
                            open.Name,
                            partial);
                        break;
                    }
                }

                break;
            }
            case "content_block_stop":
            {
                var index = frame.GetProperty("index").GetInt32();
                if (!openBlocks.TryGetValue(index, out var open))
                    break;
                openBlocks.Remove(index);
                yield return new StreamChunk.BlockEnd(index, CloseBlock(open, index));
                break;
            }
            case "message_delta":
            {
                if (frame.TryGetProperty("usage", out var usage))
                    yield return new StreamChunk.Usage(MapUsage(usage));
                break;
            }
            case "message_stop":
                break;
            case "error":
            {
                var error = frame.GetProperty("error");
                var message = error.TryGetProperty("message", out var messageProperty)
                    ? messageProperty.GetString() ?? "Anthropic stream error"
                    : "Anthropic stream error";
                throw new LlmException(new LlmFailure(message, LlmFailureCodes.Server));
            }
        }
    }

    private static OpenBlock EnsureOpen(Dictionary<int, OpenBlock> openBlocks, int index, string type)
    {
        if (openBlocks.TryGetValue(index, out var open))
            return open;
        open = new OpenBlock { Type = type };
        openBlocks[index] = open;
        return open;
    }

    private static ContentBlock CloseBlock(OpenBlock open, int index) => open.Type switch
    {
        "text" => new TextBlock(open.Text),
        "thinking" => new ReasoningBlock(open.Text),
        "tool-call" => new ToolCallBlock(
            ToolCallId.Create(open.Id ?? $"call-{index}"),
            open.Name ?? "",
            open.Arguments),
        _ => throw new InvalidOperationException($"cannot close Anthropic block of type \"{open.Type}\""),
    };

    private static TokenUsage MapUsage(JsonElement usage)
    {
        static double? Read(JsonElement element, string name)
            => element.TryGetProperty(name, out var property) ? property.GetDouble() : null;

        var input = Read(usage, "input_tokens") ?? 0;
        var output = Read(usage, "output_tokens") ?? 0;
        var cacheRead = Read(usage, "cache_read_input_tokens");
        var cacheWrite = Read(usage, "cache_creation_input_tokens");
        double? total = input is not 0 || output is not 0 ? input + output : null;

        return new TokenUsage(input, output, total, cacheRead, cacheWrite);
    }

    private sealed class OpenBlock
    {
        public required string Type;
        public string Text = "";
        public string? Id;
        public string? Name;
        public string Arguments = "";
    }
}
