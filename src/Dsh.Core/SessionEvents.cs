using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Core;

[JsonConverter(typeof(SurfaceOpJsonConverter))]
public abstract record SurfaceOp
{
    public sealed record Append : SurfaceOp;

    public sealed record Replace(long Start, long End) : SurfaceOp;
}

public sealed class SurfaceOpJsonConverter : JsonConverter<SurfaceOp>
{
    public override SurfaceOp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString() == "append"
                ? new SurfaceOp.Append()
                : throw new JsonException("invalid surfaceOp");
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.GetProperty("op").GetString() != "replace")
            throw new JsonException("invalid replace surfaceOp");
        return new SurfaceOp.Replace(root.GetProperty("start").GetInt64(), root.GetProperty("end").GetInt64());
    }

    public override void Write(Utf8JsonWriter writer, SurfaceOp value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case SurfaceOp.Append:
                writer.WriteStringValue("append");
                break;
            case SurfaceOp.Replace replace:
                writer.WriteStartObject();
                writer.WriteString("op", "replace");
                writer.WriteNumber("start", replace.Start);
                writer.WriteNumber("end", replace.End);
                writer.WriteEndObject();
                break;
        }
    }
}

public abstract record SessionEventPayload
{
    public abstract string Type { get; }
}

public sealed record TurnStartPayload(int Turn) : SessionEventPayload
{
    public override string Type => SessionEventTypes.TurnStart;
}

public sealed record TurnEndPayload(int Turn, TurnEndReason Reason) : SessionEventPayload
{
    public override string Type => SessionEventTypes.TurnEnd;
}

public sealed record StepStartPayload(int Turn, int Step) : SessionEventPayload
{
    public override string Type => SessionEventTypes.StepStart;
}

public sealed record StepEndPayload(int Turn, int Step) : SessionEventPayload
{
    public override string Type => SessionEventTypes.StepEnd;
}

public sealed record UserMessagePayload(UserMessage Message) : SessionEventPayload
{
    public override string Type => SessionEventTypes.UserMessage;
}

public sealed record AssistantChunkPayload(int Turn, int Step, StreamChunk Chunk) : SessionEventPayload
{
    public override string Type => SessionEventTypes.AssistantChunk;
}

public sealed record AssistantMessagePayload(
    int Turn,
    int Step,
    AssistantMessage Message,
    TokenUsage? Usage = null,
    bool Interrupted = false) : SessionEventPayload
{
    public override string Type => SessionEventTypes.AssistantMessage;
}

public sealed record ToolCallPayload(int Turn, int Step, ToolCallId CallId, string Name, string Arguments) : SessionEventPayload
{
    public override string Type => SessionEventTypes.ToolCall;
}

public sealed record ToolResultErrorInfo(string Name, string Code);

public sealed record ToolResultPayload(
    int Turn,
    int Step,
    ToolResultMessage Message,
    ToolResultErrorInfo? Error = null,
    JsonElement? Meta = null) : SessionEventPayload
{
    public override string Type => SessionEventTypes.ToolResult;
}

public sealed record EpochHeader(
    LlmCallConfig Config,
    LlmCallConfigAdapterDefaults? AdapterDefaults = null,
    string? System = null,
    IReadOnlyList<ToolSchema>? Tools = null);

public sealed record LlmCallConfigAdapterDefaults(bool ReasoningEffort = false, bool MaxTokens = false);

public static class RequestHeaderReasons
{
    public const string Initial = "initial";
    public const string Resume = "resume";
    public const string Change = "change";
    public const string Series = "series";
}

public sealed record RequestHeaderPayload(EpochHeader Header, string Reason, bool StartsSeries = false) : SessionEventPayload
{
    public override string Type => SessionEventTypes.RequestHeader;
}

public sealed record RequestContextPayload(string Provider, string Model, int? ContextWindow = null) : SessionEventPayload
{
    public override string Type => SessionEventTypes.RequestContext;
}

public sealed record SessionEndSeedPayload : SessionEventPayload
{
    public override string Type => SessionEventTypes.SessionEndSeed;
}

public sealed record UnknownSessionEventPayload(string RawType, JsonElement Raw) : SessionEventPayload
{
    public override string Type => RawType;
}

public static class SessionEventTypes
{
    public const string TurnStart = "turn/start";
    public const string TurnEnd = "turn/end";
    public const string StepStart = "step/start";
    public const string StepEnd = "step/end";
    public const string UserMessage = "user/message";
    public const string AssistantChunk = "assistant/chunk";
    public const string AssistantMessage = "assistant/message";
    public const string ToolCall = "tool/call";
    public const string ToolResult = "tool/result";
    public const string RequestHeader = "request/header";
    public const string RequestContext = "request/context";
    public const string SessionEndSeed = "session/end-seed";
    public const string AgentInboxSpliced = "agent/inbox/spliced";

    public static readonly IReadOnlySet<string> SurfaceEligible = new HashSet<string>
    {
        UserMessage, AssistantMessage, ToolResult,
    };
}
