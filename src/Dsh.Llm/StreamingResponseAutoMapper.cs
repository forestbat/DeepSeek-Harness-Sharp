using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Dsh.Llm;

public static class StreamingResponseAutoMapper
{
    public static async IAsyncEnumerable<StreamChunk> Translate(
        Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        var state = new MapperState();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var data = line.Trim();
            if (!data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;
            var payload = data["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
                continue;
            IReadOnlyList<StreamChunk>? mapped = null;
            try
            {
                using var document = JsonDocument.Parse(payload);
                mapped = Map(document.RootElement, state);
            }
            catch (JsonException)
            {
                // A non-JSON SSE frame carries no model content; skip it.
            }
            if (mapped is not null)
            {
                foreach (var chunk in mapped)
                    yield return chunk;
            }
        }
        foreach (var chunk in state.Finish())
            yield return chunk;
    }

    public static IReadOnlyList<StreamChunk> Map(JsonElement root, MapperState? state = null)
    {
        state ??= new MapperState();
        var chunks = new List<StreamChunk>();
        var delta = FindDelta(root);
        if (delta is { } deltaObject)
        {
            MapText(deltaObject, chunks, state);
            MapToolCalls(deltaObject, chunks, state);
        }
        else
        {
            MapText(root, chunks, state);
            MapToolCalls(root, chunks, state);
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            chunks.Add(new StreamChunk.Usage(MapUsage(usage)));
        else if (TryFindUsageInChoices(root, out var choiceUsage) && choiceUsage.ValueKind == JsonValueKind.Object)
            chunks.Add(new StreamChunk.Usage(MapUsage(choiceUsage)));
        var finishReason = FindString(root, "finish_reason", "stop_reason")
            ?? FindStringInChoices(root, "finish_reason", "stop_reason");
        if (finishReason is { Length: > 0 })
            state.FinishReason = MapFinishReason(finishReason);
        else if (FindString(root, "status", "type") is { } status && IsTerminalStatus(status))
            state.FinishReason = new FinishReason.Stop();
        return chunks;
    }

    private static JsonElement? FindDelta(JsonElement root)
    {
        if (FindObject(root, "delta", "message") is { } direct)
            return direct;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var choice in choices.EnumerateArray())
        {
            if (FindObject(choice, "delta", "message") is { } nested)
                return nested;
        }
        return null;
    }

    private static string? FindStringInChoices(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var choice in choices.EnumerateArray())
        {
            if (FindString(choice, names) is { } value)
                return value;
        }
        return null;
    }

    private static bool TryFindUsageInChoices(JsonElement root, out JsonElement usage)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("usage", out usage) && usage.ValueKind == JsonValueKind.Object)
                    return true;
            }
        }
        usage = default;
        return false;
    }

    private static void MapText(JsonElement source, List<StreamChunk> chunks, MapperState state)
    {
        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("content", out var content))
        {
            switch (content.ValueKind)
            {
                case JsonValueKind.String when content.GetString() is { Length: > 0 } text:
                    state.EnsureTextOpen(chunks);
                    chunks.Add(new StreamChunk.TextDelta(state.TextIndex, text));
                    state.AppendText(text);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("text", out var textElement))
                            continue;
                        if (textElement.GetString() is { Length: > 0 } text)
                        {
                            state.EnsureTextOpen(chunks);
                            chunks.Add(new StreamChunk.TextDelta(state.TextIndex, text));
                            state.AppendText(text);
                        }
                    }
                    break;
            }
        }

        foreach (var key in new[] { "reasoning_content", "thinking", "reasoning" })
        {
            if (!source.TryGetProperty(key, out var reasoning) || reasoning.ValueKind != JsonValueKind.String)
                continue;
            if (reasoning.GetString() is { Length: > 0 } text)
            {
                state.EnsureReasoningOpen(chunks);
                chunks.Add(new StreamChunk.ReasoningDelta(state.ReasoningIndex, text));
                state.AppendReasoning(text);
            }
        }
    }

    private static void MapToolCalls(JsonElement source, List<StreamChunk> chunks, MapperState state)
    {
        foreach (var arrayName in new[] { "tool_calls", "tools", "output" })
        {
            if (!source.TryGetProperty(arrayName, out var calls) || calls.ValueKind != JsonValueKind.Array)
                continue;
            var index = 0;
            foreach (var call in calls.EnumerateArray())
            {
                if (call.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }
                var id = FindString(call, "id") ?? $"call-{index}";
                var name = FindString(call, "name")
                    ?? (call.TryGetProperty("function", out var functionElement) && functionElement.ValueKind == JsonValueKind.Object
                        ? FindString(functionElement, "name")
                        : null);
                string? arguments = null;
                if (call.TryGetProperty("arguments", out var argumentsElement))
                {
                    arguments = argumentsElement.ValueKind switch
                    {
                        JsonValueKind.String => argumentsElement.GetString(),
                        JsonValueKind.Object => argumentsElement.GetRawText(),
                        _ => null,
                    };
                }
                else if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object
                         && function.TryGetProperty("arguments", out var functionArguments))
                {
                    arguments = functionArguments.ValueKind switch
                    {
                        JsonValueKind.String => functionArguments.GetString(),
                        JsonValueKind.Object => functionArguments.GetRawText(),
                        _ => null,
                    };
                }
                if (arguments is null)
                {
                    index++;
                    continue;
                }
                var block = state.ToolBlock(index);
                chunks.Add(new StreamChunk.ToolCallDelta(
                    block.Index,
                    block.CallId,
                    name,
                    arguments));
                if (id is not null)
                    block.CallId = ToolCallId.Create(id);
                if (name is not null)
                    block.Name = name;
                block.Arguments.Append(arguments);
                index++;
            }
        }
    }

    private static TokenUsage MapUsage(JsonElement usage)
    {
        var input = NumberOf(usage, "prompt_tokens", "input_tokens") ?? 0;
        var output = NumberOf(usage, "completion_tokens", "output_tokens") ?? 0;
        var total = NumberOf(usage, "total_tokens");
        var cacheRead = NumberOf(usage, "cache_read_input_tokens", "prompt_cache_hit_tokens");
        var cacheWrite = NumberOf(usage, "cache_creation_input_tokens", "prompt_cache_miss_tokens");
        var reasoning = NumberOf(usage, "reasoning_tokens", "completion_tokens_details.reasoning_tokens");
        return new TokenUsage(input, output, total, cacheRead, cacheWrite, reasoning);
    }

    private static FinishReason MapFinishReason(string raw) => raw switch
    {
        "stop" => new FinishReason.Stop(),
        "tool_calls" or "function_call" or "tool_use" => new FinishReason.ToolCalls(),
        "length" or "max_tokens" => new FinishReason.MaxTokens(),
        _ => new FinishReason.Unknown(raw, JsonSerializer.SerializeToElement(raw)),
    };

    private static bool IsTerminalStatus(string value)
        => value is "completed" or "succeeded" or "message_stop" or "completed" or "finished";

    private static JsonElement? FindObject(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object)
                return value;
        }
        return null;
    }

    private static string? FindString(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static double? NumberOf(JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number)
                    return value.GetDouble();
                if (name.Contains('.') && value.ValueKind == JsonValueKind.Object)
                {
                    var parts = name.Split('.');
                    var current = value;
                    var found = true;
                    foreach (var part in parts.Skip(1))
                    {
                        if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out current))
                            continue;
                        found = false;
                        break;
                    }
                    if (found && current.ValueKind == JsonValueKind.Number)
                        return current.GetDouble();
                }
            }
        }
        return null;
    }

    public sealed class MapperState
    {
        public int TextIndex { get; private set; } = -1;
        public int ReasoningIndex { get; private set; } = -1;
        private readonly Dictionary<int, ToolBlockState> _toolBlocks = [];
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _reasoning = new();
        private bool _textOpen;
        private bool _reasoningOpen;
        private FinishReason? _finishReason;

        public FinishReason? FinishReason
        {
            get => _finishReason;
            set => _finishReason = value;
        }

        public void EnsureTextOpen(List<StreamChunk> chunks)
        {
            if (_textOpen)
                return;
            TextIndex = 0;
            _textOpen = true;
            chunks.Add(new StreamChunk.BlockStart(0, "text"));
        }

        public void EnsureReasoningOpen(List<StreamChunk> chunks)
        {
            if (_reasoningOpen)
                return;
            ReasoningIndex = 1;
            _reasoningOpen = true;
            chunks.Add(new StreamChunk.BlockStart(1, "reasoning"));
        }

        public void AppendText(string text) => _text.Append(text);

        public void AppendReasoning(string text) => _reasoning.Append(text);

        public ToolBlockState ToolBlock(int index)
        {
            if (!_toolBlocks.TryGetValue(index, out var block))
            {
                block = new ToolBlockState { Index = 2 + index, CallId = ToolCallId.Create($"call-{index}") };
                _toolBlocks[index] = block;
            }
            return block;
        }

        public IReadOnlyList<StreamChunk> Finish()
        {
            var chunks = new List<StreamChunk>();
            if (_textOpen)
                chunks.Add(new StreamChunk.BlockEnd(TextIndex, new TextBlock(_text.ToString())));
            if (_reasoningOpen)
                chunks.Add(new StreamChunk.BlockEnd(ReasoningIndex, new ReasoningBlock(_reasoning.ToString())));
            foreach (var block in _toolBlocks.Values)
                chunks.Add(new StreamChunk.BlockEnd(block.Index, new ToolCallBlock(block.CallId, block.Name ?? "", block.Arguments.ToString())));
            chunks.Add(new StreamChunk.Finish(_finishReason ?? new FinishReason.Stop()));
            return chunks;
        }
    }

    public sealed class ToolBlockState
    {
        public required int Index { get; init; }
        public ToolCallId CallId { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
