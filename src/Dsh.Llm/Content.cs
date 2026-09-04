using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

[JsonConverter(typeof(ContentBlockJsonConverter))]
public abstract record ContentBlock
{
    public abstract string Type { get; }
}

public sealed record TextBlock(string Text) : ContentBlock
{
    public override string Type => "text";
}

public sealed record ReasoningBlock(string Text) : ContentBlock
{
    public override string Type => "reasoning";
}

public sealed record ImageBlock(ImageAttachmentRef Attachment) : ContentBlock
{
    public override string Type => "image";
}

public sealed record ToolCallBlock(ToolCallId Id, string Name, string Arguments) : ContentBlock
{
    public override string Type => "tool-call";
}

public sealed record ToolResultBlock(ToolCallId ToolCallId, IReadOnlyList<ContentBlock> Content, bool? IsError = null) : ContentBlock
{
    public override string Type => "tool-result";
}

public sealed record UnknownContentBlock(string RawType, JsonElement Raw) : ContentBlock
{
    public override string Type => RawType;
}

public sealed record ImageAttachmentRef(
    string AttachmentId,
    string MediaType,
    long Bytes,
    int Width,
    int Height,
    string? Name = null);

public sealed class ContentBlockJsonConverter : JsonConverter<ContentBlock>
{
    public override ContentBlock Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? throw new JsonException("content block missing \"type\"");
        return type switch
        {
            "text" => new TextBlock(root.GetProperty("text").GetString() ?? ""),
            "reasoning" => new ReasoningBlock(root.GetProperty("text").GetString() ?? ""),
            "image" => new ImageBlock(root.GetProperty("attachment").Deserialize<ImageAttachmentRef>(options)
                ?? throw new JsonException("image block missing attachment")),
            "tool-call" => new ToolCallBlock(
                ToolCallId.Create(root.GetProperty("id").GetString() ?? throw new JsonException("tool-call missing id")),
                root.GetProperty("name").GetString() ?? "",
                root.GetProperty("arguments").GetString() ?? ""),
            "tool-result" => new ToolResultBlock(
                ToolCallId.Create(root.GetProperty("toolCallId").GetString() ?? throw new JsonException("tool-result missing toolCallId")),
                root.GetProperty("content").Deserialize<IReadOnlyList<ContentBlock>>(options) ?? [],
                root.TryGetProperty("isError", out var isError) ? isError.GetBoolean() : null),
            _ => new UnknownContentBlock(type, root.Clone()),
        };
    }

    public override void Write(Utf8JsonWriter writer, ContentBlock value, JsonSerializerOptions options)
    {
        if (value is UnknownContentBlock unknown)
        {
            unknown.Raw.WriteTo(writer);
            return;
        }
        writer.WriteStartObject();
        writer.WriteString("type", value.Type);
        switch (value)
        {
            case TextBlock text:
                writer.WriteString("text", text.Text);
                break;
            case ReasoningBlock reasoning:
                writer.WriteString("text", reasoning.Text);
                break;
            case ImageBlock image:
                writer.WritePropertyName("attachment");
                JsonSerializer.Serialize(writer, image.Attachment, options);
                break;
            case ToolCallBlock call:
                writer.WriteString("id", call.Id.Value);
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.Arguments);
                break;
            case ToolResultBlock result:
                writer.WriteString("toolCallId", result.ToolCallId.Value);
                writer.WritePropertyName("content");
                JsonSerializer.Serialize(writer, result.Content, options);
                if (result.IsError is { } isError)
                    writer.WriteBoolean("isError", isError);
                break;
        }
        writer.WriteEndObject();
    }
}
