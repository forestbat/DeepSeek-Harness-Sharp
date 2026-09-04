using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

public sealed class MessageJsonConverter : JsonConverter<Message>
{
    public override Message Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var id = MessageId.Create(root.GetProperty("id").GetString() ?? throw new JsonException("message missing id"));
        var roleText = root.GetProperty("role").GetString() ?? throw new JsonException("message missing role");
        var content = root.GetProperty("content").Deserialize<IReadOnlyList<ContentBlock>>(options)
            ?? throw new JsonException("message missing content");
        var source = root.GetProperty("source").Deserialize<MessageSource>(options)
            ?? throw new JsonException("message missing source");
        var role = roleText switch
        {
            "system" => MessageRole.System,
            "user" => MessageRole.User,
            "assistant" => MessageRole.Assistant,
            _ => throw new JsonException($"unknown message role \"{roleText}\""),
        };
        if (role == MessageRole.Assistant && source is ModelMessageSource modelSource)
        {
            return new AssistantMessage { Id = id, Content = content, ModelSource = modelSource };
        }
        if (role == MessageRole.User && source is ToolMessageSource toolSource)
        {
            return new ToolResultMessage { Id = id, Content = content, ToolSource = toolSource };
        }
        if (role == MessageRole.User)
            return new UserMessage { Id = id, Content = content, Source = source };
        return new Message { Id = id, Role = role, Content = content, Source = source };
    }

    public override void Write(Utf8JsonWriter writer, Message value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("id", value.Id.Value);
        writer.WriteString("role", value.Role switch
        {
            MessageRole.System => "system",
            MessageRole.User => "user",
            MessageRole.Assistant => "assistant",
            _ => throw new JsonException($"unknown message role {value.Role}"),
        });
        writer.WritePropertyName("content");
        JsonSerializer.Serialize(writer, value.Content, options);
        writer.WritePropertyName("source");
        JsonSerializer.Serialize(writer, value.Source, options);
        writer.WriteEndObject();
    }
}
