using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Llm;

/** Discriminators shared by serialization and non-.NET renderers. */
public static class ContentBlockTypes
{
    public const string Text = "text";
    public const string Image = "image";
    public const string ToolResult = "tool-result";
}

[JsonConverter(typeof(ContentBlockJsonConverter))]
public abstract record ContentBlock
{
    public abstract string Type { get; }
}

public sealed record TextBlock(string Text) : ContentBlock
{
    public override string Type => ContentBlockTypes.Text;
}

/**
 * 思维链块。
 * ProtectedData / ItemId 是 OpenAI 兼容层(MEAI)对"服务端要求原样回传"的推理凭据的通用载体:
 * Responses 的 reasoning item 加密内容与 item id、Chat Completions 各家的签名都从这里过。
 * 缺省为 null 时序列化不落字段, 老会话与只产出纯文本的供应商行为不变。
 */
public sealed record ReasoningBlock(string Text, string? ProtectedData = null, string? ItemId = null) : ContentBlock
{
    public override string Type => "reasoning";
}

public sealed record ImageBlock(ImageAttachmentRef Attachment) : ContentBlock
{
    public override string Type => ContentBlockTypes.Image;
}

public sealed record ToolCallBlock(ToolCallId Id, string Name, string Arguments) : ContentBlock
{
    public override string Type => "tool-call";
}

public sealed record ToolResultBlock(ToolCallId ToolCallId, IReadOnlyList<ContentBlock> Content, bool? IsError = null) : ContentBlock
{
    public override string Type => ContentBlockTypes.ToolResult;
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
            ContentBlockTypes.Text => new TextBlock(root.GetProperty("text").GetString() ?? ""),
            "reasoning" => new ReasoningBlock(
                root.GetProperty("text").GetString() ?? "",
                root.TryGetProperty("protectedData", out var protectedData) ? protectedData.GetString() : null,
                root.TryGetProperty("itemId", out var itemId) ? itemId.GetString() : null),
            ContentBlockTypes.Image => new ImageBlock(DshJson.Deserialize<ImageAttachmentRef>(root.GetProperty("attachment"))
                ?? throw new JsonException("image block missing attachment")),
            "tool-call" => new ToolCallBlock(
                ToolCallId.Create(root.GetProperty("id").GetString() ?? throw new JsonException("tool-call missing id")),
                root.GetProperty("name").GetString() ?? "",
                root.GetProperty("arguments").GetString() ?? ""),
            ContentBlockTypes.ToolResult => new ToolResultBlock(
                ToolCallId.Create(root.GetProperty("toolCallId").GetString() ?? throw new JsonException("tool-result missing toolCallId")),
                DshJson.Deserialize<IReadOnlyList<ContentBlock>>(root.GetProperty("content")) ?? [],
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
                if (reasoning.ProtectedData is { } protectedData)
                    writer.WriteString("protectedData", protectedData);
                if (reasoning.ItemId is { } itemId)
                    writer.WriteString("itemId", itemId);
                break;
            case ImageBlock image:
                writer.WritePropertyName("attachment");
                DshJson.Serialize(writer, image.Attachment);
                break;
            case ToolCallBlock call:
                writer.WriteString("id", call.Id.Value);
                writer.WriteString("name", call.Name);
                writer.WriteString("arguments", call.Arguments);
                break;
            case ToolResultBlock result:
                writer.WriteString("toolCallId", result.ToolCallId.Value);
                writer.WritePropertyName("content");
                DshJson.Serialize(writer, result.Content);
                if (result.IsError is { } isError)
                    writer.WriteBoolean("isError", isError);
                break;
        }
        writer.WriteEndObject();
    }
}
