using System.Text.Json;
using System.Text.Json.Nodes;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Gui.Shared.Mobile;

/** 手机 agent 触碰外界的两类工具共用的结果形状: 一句给模型看的文本, 加成功与否。 */
public readonly record struct MobileToolAnswer(bool Ok, string Text)
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
        Func<JsonElement, CancellationToken, Task<MobileToolAnswer>> run)
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
                    },
                },
                (_, value) => [new TextBlock(value.GetProperty("text").GetString() ?? "")]),
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
                return new JsonObject { ["ok"] = answer.Ok, ["text"] = answer.Text };
            },
        };

    public static string? Text(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) ? value.GetString() : null;
}
