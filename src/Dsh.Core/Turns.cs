using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Llm;

namespace Dsh.Core;

[JsonConverter(typeof(AgentCancelCauseJsonConverter))]
public abstract record AgentCancelCause
{
    public abstract string Kind { get; }

    public sealed record User : AgentCancelCause
    {
        public override string Kind => "user";
    }

    public sealed record Parent : AgentCancelCause
    {
        public override string Kind => "parent";
    }

    public sealed record Hook(string Reason) : AgentCancelCause
    {
        public override string Kind => "hook";
    }

    public sealed record Disposed : AgentCancelCause
    {
        public override string Kind => "disposed";
    }

    public sealed record Legacy : AgentCancelCause
    {
        public override string Kind => "legacy";
    }
}

public sealed class AgentCancelCauseJsonConverter : JsonConverter<AgentCancelCause>
{
    public override AgentCancelCause Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var kind = root.GetProperty("kind").GetString() ?? throw new JsonException("cancel cause missing \"kind\"");
        return kind switch
        {
            "user" => new AgentCancelCause.User(),
            "parent" => new AgentCancelCause.Parent(),
            "hook" => new AgentCancelCause.Hook(root.GetProperty("reason").GetString() ?? ""),
            "disposed" => new AgentCancelCause.Disposed(),
            "legacy" => new AgentCancelCause.Legacy(),
            _ => throw new JsonException($"unknown cancel cause kind \"{kind}\""),
        };
    }

    public override void Write(Utf8JsonWriter writer, AgentCancelCause value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        if (value is AgentCancelCause.Hook hook)
            writer.WriteString("reason", hook.Reason);
        writer.WriteEndObject();
    }
}

[JsonConverter(typeof(TurnEndReasonJsonConverter))]
public abstract record TurnEndReason
{
    public abstract string Kind { get; }

    public sealed record Completed : TurnEndReason
    {
        public override string Kind => "completed";
    }

    public sealed record Aborted(AgentCancelCause Reason) : TurnEndReason
    {
        public override string Kind => "aborted";
    }

    public sealed record Blocked : TurnEndReason
    {
        public override string Kind => "blocked";
    }

    public sealed record Error(LlmFailure Failure) : TurnEndReason
    {
        public override string Kind => "error";
    }

    public sealed record MaxTokens : TurnEndReason
    {
        public override string Kind => "max-tokens";
    }

    public sealed record Interrupted : TurnEndReason
    {
        public override string Kind => "interrupted";
    }

    public sealed record Unknown(string RawKind, JsonElement Raw) : TurnEndReason
    {
        public override string Kind => RawKind;
    }
}

public sealed class TurnEndReasonJsonConverter : JsonConverter<TurnEndReason>
{
    public override TurnEndReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var kind = root.GetProperty("kind").GetString() ?? throw new JsonException("turn end reason missing \"kind\"");
        return kind switch
        {
            "completed" => new TurnEndReason.Completed(),
            "aborted" => new TurnEndReason.Aborted(
                root.GetProperty("reason").Deserialize<AgentCancelCause>(options)
                ?? throw new JsonException("aborted turn end missing reason")),
            "blocked" => new TurnEndReason.Blocked(),
            "error" => new TurnEndReason.Error(
                root.GetProperty("error").Deserialize<LlmFailure>(options)
                ?? throw new JsonException("error turn end missing failure")),
            "max-tokens" => new TurnEndReason.MaxTokens(),
            "interrupted" => new TurnEndReason.Interrupted(),
            _ => new TurnEndReason.Unknown(kind, root.Clone()),
        };
    }

    public override void Write(Utf8JsonWriter writer, TurnEndReason value, JsonSerializerOptions options)
    {
        if (value is TurnEndReason.Unknown unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case TurnEndReason.Aborted aborted:
                writer.WritePropertyName("reason");
                JsonSerializer.Serialize(writer, aborted.Reason, options);
                break;
            case TurnEndReason.Error error:
                writer.WritePropertyName("error");
                JsonSerializer.Serialize(writer, error.Failure, options);
                break;
        }
        writer.WriteEndObject();
    }
}

public sealed record SessionHeader
{
    public const int SessionFormatVersion = 0;

    public required int Version { get; init; }
    public required SessionId Id { get; init; }
    public required long CreatedAt { get; init; }
    public string? Cwd { get; init; }
    public SessionId? ParentSession { get; init; }
    public required bool IsSeeded { get; init; }
    public string? Origin { get; init; }
    public int? DelegationDepth { get; init; }
    public string? AgentPreset { get; init; }

    public void Validate()
    {
        if (Version != SessionFormatVersion)
            throw new JsonException($"session header version must be {SessionFormatVersion}, got {Version}");
        if (CreatedAt < 0)
            throw new JsonException("session header createdAt must be a non-negative safe integer");
        if (Cwd is not null && !Path.IsPathRooted(Cwd))
            throw new JsonException($"session header cwd must be an absolute path, got \"{Cwd}\"");
        if (Origin is not null && Origin != "subagent")
            throw new JsonException("session header origin must be \"subagent\"");
        if (DelegationDepth is < 0)
            throw new JsonException("session header delegationDepth must be a non-negative safe integer");
    }
}
