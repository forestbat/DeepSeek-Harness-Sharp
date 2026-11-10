using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Gui.Shared.Mobile;

/** 手机 agent 触碰外界的两类工具共用的结果形状: 一句给模型看的文本, 加成功与否, 可带一张截图。 */
public readonly record struct MobileToolAnswer(bool Ok, string Text, PhoneScreenshot? Screenshot = null)
{
    public static MobileToolAnswer Success(string text) => new(true, text);

    public static MobileToolAnswer Failure(string text) => new(false, text);
}

/** 工具输出的 schema 与 render 必须和答案形状一致, 所以由这里统一产出, 各处只写业务。 */
public static class MobileTool
{
    public static ToolDefinition Create(
        string name,
        string description,
        JsonObject parameters,
        Func<JsonElement, CancellationToken, Task<MobileToolAnswer>> run,
        IAttachmentStore? attachments = null)
        => new()
        {
            Name = name,
            Description = description,
            Parameters = parameters,
            Output = new ToolOutputDefinition(
                new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["required"] = new JsonArray("ok", "text"),
                    ["properties"] = new JsonObject
                    {
                        ["ok"] = new JsonObject { ["type"] = "boolean", ["description"] = "调用是否成功" },
                        ["text"] = new JsonObject { ["type"] = "string", ["description"] = "给模型看的结果文本" },
                        ["image"] = ImageSchema(),
                    },
                },
                (_, value) => Render(value)),
            Execute = async (args, context) =>
            {
                MobileToolAnswer answer;
                try
                {
                    answer = await run(args, context.Signal).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    answer = MobileToolAnswer.Failure(error.Message);
                }
                var result = new JsonObject { ["ok"] = answer.Ok, ["text"] = answer.Text };
                if (answer.Screenshot is { } shot && attachments is not null)
                    result["image"] = Describe(attachments.Put(shot.Png, PngMediaType, shot.Width, shot.Height, ScreenshotName));
                return result;
            },
        };

    private const string PngMediaType = "image/png";
    private const string ScreenshotName = "phone-screen.png";

    public static string? Text(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) ? value.GetString() : null;

    public static int? Int(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    /** 有图时文本之外再返回一个 ImageBlock: 自绘界面只能让模型看画面。 */
    private static IReadOnlyList<ContentBlock> Render(JsonElement value)
    {
        var text = new TextBlock(value.GetProperty("text").GetString() ?? "");
        if (!value.TryGetProperty("image", out var image) || image.ValueKind != JsonValueKind.Object)
            return [text];
        var reference = DshJson.Deserialize<ImageAttachmentRef>(image);
        return reference is null ? [text] : [text, new ImageBlock(reference)];
    }

    private static JsonObject Describe(ImageAttachmentRef reference) => new()
    {
        ["attachmentId"] = reference.AttachmentId,
        ["mediaType"] = reference.MediaType,
        ["bytes"] = reference.Bytes,
        ["width"] = reference.Width,
        ["height"] = reference.Height,
        ["name"] = reference.Name,
    };

    private static JsonObject ImageSchema() => new()
    {
        ["type"] = "object",
        ["additionalProperties"] = false,
        ["required"] = new JsonArray("attachmentId", "mediaType", "bytes", "width", "height"),
        ["properties"] = new JsonObject
        {
            ["attachmentId"] = new JsonObject { ["type"] = "string" },
            ["mediaType"] = new JsonObject { ["type"] = "string" },
            ["bytes"] = new JsonObject { ["type"] = "integer" },
            ["width"] = new JsonObject { ["type"] = "integer" },
            ["height"] = new JsonObject { ["type"] = "integer" },
            ["name"] = new JsonObject { ["type"] = "string" },
        },
    };
}
