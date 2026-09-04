using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dsh.Llm.DeepSeek;

public sealed record WireRequest
{
    [JsonPropertyName("model")] public required string Model { get; init; }
    [JsonPropertyName("messages")] public required IReadOnlyList<WireMessage> Messages { get; init; }
    [JsonPropertyName("stream")] public bool Stream => true;
    [JsonPropertyName("stream_options")] public JsonObject StreamOptions => new() { ["include_usage"] = true };
    [JsonPropertyName("thinking")] public JsonObject? Thinking { get; init; }
    [JsonPropertyName("reasoning_effort")] public string? ReasoningEffort { get; init; }
    [JsonPropertyName("tools")] public IReadOnlyList<WireTool>? Tools { get; init; }
    [JsonPropertyName("temperature")] public double? Temperature { get; init; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
    [JsonPropertyName("stop")] public IReadOnlyList<string>? Stop { get; init; }
}

public abstract record WireMessage
{
    public abstract string Role { get; }

    public sealed record System(string Content) : WireMessage
    {
        public override string Role => "system";
    }

    public sealed record User(string Content) : WireMessage
    {
        public override string Role => "user";
    }

    public sealed record Assistant(string Content, string? ReasoningContent, IReadOnlyList<WireToolCall>? ToolCalls) : WireMessage
    {
        public override string Role => "assistant";
    }

    public sealed record Tool(ToolCallId ToolCallId, string Content) : WireMessage
    {
        public override string Role => "tool";
    }
}

public sealed record WireToolCall(string Id, string Name, string Arguments);

public sealed record WireTool(string Name, string Description, JsonObject Parameters);

public sealed class WireChunk
{
    [JsonPropertyName("choices")] public List<WireChoice>? Choices { get; set; }
    [JsonPropertyName("usage")] public WireUsage? Usage { get; set; }
}

public sealed class WireChoice
{
    [JsonPropertyName("delta")] public WireDelta? Delta { get; set; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
}

public sealed class WireDelta
{
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("reasoning_content")] public string? ReasoningContent { get; set; }
    [JsonPropertyName("tool_calls")] public List<WireToolCallDelta>? ToolCalls { get; set; }
}

public sealed class WireToolCallDelta
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("function")] public WireToolCallDeltaFunction? Function { get; set; }
}

public sealed class WireToolCallDeltaFunction
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("arguments")] public string? Arguments { get; set; }
}

public sealed class WireUsage
{
    [JsonPropertyName("prompt_tokens")] public double PromptTokens { get; set; }
    [JsonPropertyName("completion_tokens")] public double CompletionTokens { get; set; }
    [JsonPropertyName("total_tokens")] public double? TotalTokens { get; set; }
    [JsonPropertyName("prompt_cache_hit_tokens")] public double? PromptCacheHitTokens { get; set; }
    [JsonPropertyName("prompt_tokens_details")] public WirePromptTokensDetails? PromptTokensDetails { get; set; }
    [JsonPropertyName("completion_tokens_details")] public WireCompletionTokensDetails? CompletionTokensDetails { get; set; }
}

public sealed class WirePromptTokensDetails
{
    [JsonPropertyName("cached_tokens")] public double? CachedTokens { get; set; }
}

public sealed class WireCompletionTokensDetails
{
    [JsonPropertyName("reasoning_tokens")] public double? ReasoningTokens { get; set; }
}

public sealed class WireError
{
    [JsonPropertyName("error")] public WireErrorBody? Error { get; set; }
}

public sealed class WireErrorBody
{
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}
