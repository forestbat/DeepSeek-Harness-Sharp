using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

[JsonConverter(typeof(MessageSourceJsonConverter))]
public abstract record MessageSource
{
    public abstract string Kind { get; }
}

public sealed record UserMessageSource : MessageSource
{
    public override string Kind => "user";
}

public sealed record PluginMessageSource(
    string Plugin,
    string? Form = null,
    IReadOnlyList<ContextSnapshotSection>? Sections = null,
    string? Summary = null) : MessageSource
{
    public override string Kind => "plugin";
}

public sealed record ModelMessageSource(string Provider, string Model, JsonElement? ReplayState = null) : MessageSource
{
    public override string Kind => "model";
}

public sealed record ToolMessageSource(ToolCallId CallId) : MessageSource
{
    public override string Kind => "tool";
}

public sealed record UnknownMessageSource(string RawKind, JsonElement Raw) : MessageSource
{
    public override string Kind => RawKind;
}

public sealed record ContextSnapshotSection(string Name, string Text);

public static class ContextForms
{
    public const string Instructions = "instructions";
    public const string Catalog = "catalog";
    public const string Snapshot = "snapshot";
    public const string Notice = "notice";
    public const string Relay = "relay";
    public const string Recall = "recall";
}

public sealed class MessageSourceJsonConverter : JsonConverter<MessageSource>
{
    public override MessageSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var kind = root.GetProperty("kind").GetString() ?? throw new JsonException("message source missing \"kind\"");
        return kind switch
        {
            "user" => new UserMessageSource(),
            "plugin" => new PluginMessageSource(
                root.GetProperty("plugin").GetString() ?? throw new JsonException("plugin source missing plugin"),
                root.TryGetProperty("form", out var form) ? form.GetString() : null,
                root.TryGetProperty("sections", out var sections)
                    ? sections.Deserialize<IReadOnlyList<ContextSnapshotSection>>(options) : null,
                root.TryGetProperty("summary", out var summary) ? summary.GetString() : null),
            "model" => new ModelMessageSource(
                root.GetProperty("provider").GetString() ?? throw new JsonException("model source missing provider"),
                root.GetProperty("model").GetString() ?? throw new JsonException("model source missing model"),
                root.TryGetProperty("replayState", out var replay) ? replay.Clone() : null),
            "tool" => new ToolMessageSource(ToolCallId.Create(
                root.GetProperty("callId").GetString() ?? throw new JsonException("tool source missing callId"))),
            _ => new UnknownMessageSource(kind, root.Clone()),
        };
    }

    public override void Write(Utf8JsonWriter writer, MessageSource value, JsonSerializerOptions options)
    {
        if (value is UnknownMessageSource unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("kind", value.Kind);
        switch (value)
        {
            case PluginMessageSource plugin:
                writer.WriteString("plugin", plugin.Plugin);
                if (plugin.Form is { } form)
                    writer.WriteString("form", form);
                if (plugin.Sections is { } sections)
                {
                    writer.WritePropertyName("sections");
                    JsonSerializer.Serialize(writer, sections, options);
                }
                if (plugin.Summary is { } summary)
                    writer.WriteString("summary", summary);
                break;
            case ModelMessageSource model:
                writer.WriteString("provider", model.Provider);
                writer.WriteString("model", model.Model);
                if (model.ReplayState is { } replay)
                {
                    writer.WritePropertyName("replayState");
                    replay.WriteTo(writer);
                }
                break;
            case ToolMessageSource tool:
                writer.WriteString("callId", tool.CallId.Value);
                break;
        }
        writer.WriteEndObject();
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<MessageRole>))]
public enum MessageRole
{
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("assistant")] Assistant,
}

[JsonConverter(typeof(MessageJsonConverter))]
public record Message
{
    public required MessageId Id { get; init; }
    public MessageRole Role { get; init; }
    public required IReadOnlyList<ContentBlock> Content { get; init; }
    public MessageSource Source { get; init; } = new UserMessageSource();
}

public sealed record UserMessage : Message
{
    public UserMessage()
    {
        Role = MessageRole.User;
    }
}

public sealed record AssistantMessage : Message
{
    public AssistantMessage()
    {
        Role = MessageRole.Assistant;
    }

    public required ModelMessageSource ModelSource
    {
        get => (ModelMessageSource)Source;
        init => Source = value;
    }
}

public sealed record ToolResultMessage : Message
{
    public ToolResultMessage()
    {
        Role = MessageRole.User;
    }

    public ToolResultBlock Block => (ToolResultBlock)Content[0];

    public ToolMessageSource ToolSource
    {
        get => (ToolMessageSource)Source;
        init => Source = value;
    }
}

public static class MessageFactory
{
    public const int ContextSummaryMaxChars = 120;

    public static string BoundContextSummary(string summary)
        => summary.Length <= ContextSummaryMaxChars ? summary : $"{summary[..(ContextSummaryMaxChars - 1)]}…";

    public static MessageId NewId() => MessageId.Create(Guid.NewGuid().ToString());

    public static UserMessage CreateUserMessage(IReadOnlyList<ContentBlock> content, MessageSource? source = null)
        => new() { Id = NewId(), Content = content, Source = source ?? new UserMessageSource() };

    public static UserMessage CreateUserText(string text, MessageSource? source = null)
        => CreateUserMessage([new TextBlock(text)], source);

    public static AssistantMessage CreateAssistantMessage(IReadOnlyList<ContentBlock> content, string provider, string model, JsonElement? replayState = null)
        => new() { Id = NewId(), Content = content, ModelSource = new ModelMessageSource(provider, model, replayState) };

    public static ToolResultMessage CreateToolResultMessage(ToolCallId callId, IReadOnlyList<ContentBlock> content, bool isError)
    {
        var block = new ToolResultBlock(callId, content, isError);
        return new ToolResultMessage
        {
            Id = NewId(),
            Content = [block],
            ToolSource = new ToolMessageSource(callId),
        };
    }
}
