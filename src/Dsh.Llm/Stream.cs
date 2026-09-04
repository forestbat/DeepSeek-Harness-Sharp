using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

public sealed record TokenUsage(
    double InputTokens,
    double OutputTokens,
    double? TotalTokens = null,
    double? CacheReadTokens = null,
    double? CacheWriteTokens = null,
    double? ReasoningTokens = null);

[JsonConverter(typeof(FinishReasonJsonConverter))]
public abstract record FinishReason
{
    public abstract string Kind { get; }

    public sealed record Stop : FinishReason
    {
        public override string Kind => "stop";
    }

    public sealed record ToolCalls : FinishReason
    {
        public override string Kind => "tool-calls";
    }

    public sealed record MaxTokens : FinishReason
    {
        public override string Kind => "max-tokens";
    }

    public sealed record Aborted(LlmFailure Failure) : FinishReason
    {
        public override string Kind => "aborted";
    }

    public sealed record Error(LlmFailure Failure) : FinishReason
    {
        public override string Kind => "error";
    }

    public sealed record Unknown(string RawKind, JsonElement Raw) : FinishReason
    {
        public override string Kind => RawKind;
    }
}

public sealed class FinishReasonJsonConverter : JsonConverter<FinishReason>
{
    public override FinishReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var kind = root.GetProperty("kind").GetString() ?? throw new JsonException("finish reason missing \"kind\"");
        LlmFailure Failure() => root.GetProperty("failure").Deserialize<LlmFailure>(options)
            ?? throw new JsonException("finish reason missing failure");
        return kind switch
        {
            "stop" => new FinishReason.Stop(),
            "tool-calls" => new FinishReason.ToolCalls(),
            "max-tokens" => new FinishReason.MaxTokens(),
            "aborted" => new FinishReason.Aborted(Failure()),
            "error" => new FinishReason.Error(Failure()),
            _ => new FinishReason.Unknown(kind, root.Clone()),
        };
    }

    public override void Write(Utf8JsonWriter writer, FinishReason value, JsonSerializerOptions options)
    {
        if (value is FinishReason.Unknown unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case FinishReason.Aborted aborted:
                writer.WritePropertyName("failure");
                JsonSerializer.Serialize(writer, aborted.Failure, options);
                break;
            case FinishReason.Error error:
                writer.WritePropertyName("failure");
                JsonSerializer.Serialize(writer, error.Failure, options);
                break;
        }
        writer.WriteEndObject();
    }
}

public sealed record ReplayEnvelope(JsonElement Response, IReadOnlyList<JsonElement>? Blocks = null);

[JsonConverter(typeof(StreamChunkJsonConverter))]
public abstract record StreamChunk
{
    public abstract string Type { get; }

    public sealed record BlockStart(int Index, string BlockType) : StreamChunk
    {
        public override string Type => "block-start";
    }

    public sealed record TextDelta(int Index, string Text) : StreamChunk
    {
        public override string Type => "text-delta";
    }

    public sealed record ReasoningDelta(int Index, string Text) : StreamChunk
    {
        public override string Type => "reasoning-delta";
    }

    public sealed record ToolCallDelta(int Index, ToolCallId Id, string? Name, string ArgumentsDelta) : StreamChunk
    {
        public override string Type => "tool-call-delta";
    }

    public sealed record BlockEnd(int Index, ContentBlock Block) : StreamChunk
    {
        public override string Type => "block-end";
    }

    public sealed record Usage(TokenUsage Value) : StreamChunk
    {
        public override string Type => "usage";
    }

    public sealed record Finish(FinishReason Reason, ReplayEnvelope? ReplayState = null) : StreamChunk
    {
        public override string Type => "finish";
    }
}

public sealed class StreamChunkJsonConverter : JsonConverter<StreamChunk>
{
    public override StreamChunk Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? throw new JsonException("stream chunk missing \"type\"");
        int Index() => root.GetProperty("index").GetInt32();
        return type switch
        {
            "block-start" => new StreamChunk.BlockStart(Index(), root.GetProperty("blockType").GetString() ?? ""),
            "text-delta" => new StreamChunk.TextDelta(Index(), root.GetProperty("text").GetString() ?? ""),
            "reasoning-delta" => new StreamChunk.ReasoningDelta(Index(), root.GetProperty("text").GetString() ?? ""),
            "tool-call-delta" => new StreamChunk.ToolCallDelta(
                Index(),
                ToolCallId.Create(root.GetProperty("id").GetString() ?? throw new JsonException("tool-call-delta missing id")),
                root.TryGetProperty("name", out var name) ? name.GetString() : null,
                root.GetProperty("argumentsDelta").GetString() ?? ""),
            "block-end" => new StreamChunk.BlockEnd(Index(), root.GetProperty("block").Deserialize<ContentBlock>(options)
                ?? throw new JsonException("block-end missing block")),
            "usage" => new StreamChunk.Usage(root.GetProperty("usage").Deserialize<TokenUsage>(options)
                ?? throw new JsonException("usage chunk missing usage")),
            "finish" => new StreamChunk.Finish(
                root.GetProperty("reason").Deserialize<FinishReason>(options)
                    ?? throw new JsonException("finish chunk missing reason"),
                root.TryGetProperty("replayState", out var replay)
                    ? replay.Deserialize<ReplayEnvelope>(options) : null),
            _ => throw new JsonException($"unknown stream chunk type \"{type}\""),
        };
    }

    public override void Write(Utf8JsonWriter writer, StreamChunk value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        switch (value)
        {
            case StreamChunk.BlockStart blockStart:
                writer.WriteNumber("index", blockStart.Index);
                writer.WriteString("blockType", blockStart.BlockType);
                break;
            case StreamChunk.TextDelta textDelta:
                writer.WriteNumber("index", textDelta.Index);
                writer.WriteString("text", textDelta.Text);
                break;
            case StreamChunk.ReasoningDelta reasoningDelta:
                writer.WriteNumber("index", reasoningDelta.Index);
                writer.WriteString("text", reasoningDelta.Text);
                break;
            case StreamChunk.ToolCallDelta toolCallDelta:
                writer.WriteNumber("index", toolCallDelta.Index);
                writer.WriteString("id", toolCallDelta.Id.Value);
                if (toolCallDelta.Name is { } name)
                    writer.WriteString("name", name);
                writer.WriteString("argumentsDelta", toolCallDelta.ArgumentsDelta);
                break;
            case StreamChunk.BlockEnd blockEnd:
                writer.WriteNumber("index", blockEnd.Index);
                writer.WritePropertyName("block");
                JsonSerializer.Serialize(writer, blockEnd.Block, options);
                break;
            case StreamChunk.Usage usage:
                writer.WritePropertyName("usage");
                JsonSerializer.Serialize(writer, usage.Value, options);
                break;
            case StreamChunk.Finish finish:
                writer.WritePropertyName("reason");
                JsonSerializer.Serialize(writer, finish.Reason, options);
                if (finish.ReplayState is { } replay)
                {
                    writer.WritePropertyName("replayState");
                    JsonSerializer.Serialize(writer, replay, options);
                }
                break;
        }
        writer.WriteEndObject();
    }
}
