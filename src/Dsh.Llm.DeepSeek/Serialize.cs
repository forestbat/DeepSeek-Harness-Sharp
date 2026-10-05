using System.Text.Json.Nodes;

namespace Dsh.Llm.DeepSeek;

public sealed record RequestDefaults(string? Thinking = null, string? ReasoningEffort = null);

public static class WireSerialize
{
    private static string ResolveReasoningEffort(ReasoningEffortId effort)
        => effort.Value is "off" or "low" or "high" or "max"
            ? effort.Value
            : throw new LlmException(new LlmFailure(
                $"DeepSeek does not support reasoning effort \"{effort}\"",
                "UNSUPPORTED_REASONING_EFFORT"));

    private static (string? Thinking, string? ReasoningEffort) ResolveThinking(GenerateOptions options, RequestDefaults defaults)
    {
        if (options.Purpose is GeneratePurpose.SessionTitle)
            return ("disabled", null);
        var effort = options.ReasoningEffort is { } selected
            ? ResolveReasoningEffort(selected)
            : defaults.ReasoningEffort;
        if (defaults.Thinking == "disabled" && effort is not (null or "off"))
        {
            throw new LlmException(new LlmFailure(
                $"DeepSeek deployment does not support reasoning effort \"{effort}\"",
                "UNSUPPORTED_REASONING_EFFORT"));
        }
        return effort switch
        {
            "off" => ("disabled", null),
            "low" or "high" or "max" => ("enabled", effort),
            _ => (defaults.Thinking, null),
        };
    }

    private static string FlattenText(IReadOnlyList<ContentBlock> blocks)
        => string.Concat(blocks.OfType<TextBlock>().Select(block => block.Text));

    // 无图时 content 为字符串; 有图时按 OpenAI 兼容的多模态 parts 数组发送(image_url data URI)。
    private static JsonNode UserContent(GenerateOptions options, IReadOnlyList<ContentBlock> blocks)
    {
        if (!blocks.Any(block => block is ImageBlock))
            return JsonValue.Create(FlattenText(blocks)) ?? JsonValue.Create("");
        var parts = new JsonArray();
        var text = FlattenText(blocks);
        if (text.Length > 0)
            parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
        foreach (var image in blocks.OfType<ImageBlock>())
        {
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = AttachmentResolution.DataUrl(options, image.Attachment) },
            });
        }
        return parts;
    }

    private static WireMessage SerializeAssistant(Message message)
    {
        var text = FlattenText(message.Content);
        var reasoning = string.Concat(message.Content.OfType<ReasoningBlock>().Select(block => block.Text));
        var toolCalls = message.Content
            .OfType<ToolCallBlock>()
            .Select(block => new WireToolCall(block.Id.Value, new WireToolCallFunction(block.Name, block.Arguments)))
            .ToList();
        return new WireMessage(
            "assistant",
            JsonValue.Create(text),
            reasoning.Length > 0 ? reasoning : null,
            toolCalls.Count > 0 ? toolCalls : null);
    }

    public static IReadOnlyList<WireMessage> SerializeMessages(GenerateOptions options)
    {
        var wire = new List<WireMessage>();
        foreach (var message in options.Messages)
        {
            switch (message.Role)
            {
                case MessageRole.System:
                    wire.Add(new WireMessage("system", JsonValue.Create(FlattenText(message.Content))));
                    continue;
                case MessageRole.Assistant:
                    wire.Add(SerializeAssistant(message));
                    continue;
                default:
                    {
                        var toolResults = message.Content.OfType<ToolResultBlock>().ToList();
                        var text = FlattenText(message.Content);
                        var hasImages = message.Content.Any(block => block is ImageBlock);
                        if (text.Length > 0 || hasImages || toolResults.Count == 0)
                            wire.Add(new WireMessage("user", UserContent(options, message.Content)));
                        foreach (var result in toolResults)
                        {
                            var content = FlattenText(result.Content);
                            wire.Add(new WireMessage("tool", JsonValue.Create(content.Length > 0 ? content : "(no output)"), ToolCallId: result.ToolCallId.Value));
                        }
                        break;
                    }
            }
        }
        return wire;
    }

    public static WireRequest SerializeRequest(GenerateOptions options, RequestDefaults defaults)
    {
        var messages = new List<WireMessage>();
        if (options.System is { } system)
            messages.Add(new WireMessage("system", JsonValue.Create(system)));
        messages.AddRange(SerializeMessages(options));
        var (thinking, reasoningEffort) = ResolveThinking(options, defaults);
        return new WireRequest
        {
            Model = options.Model,
            Messages = messages,
            Thinking = thinking is null ? null : new JsonObject { ["type"] = thinking },
            ReasoningEffort = reasoningEffort,
            Tools = options.Tools is { Count: > 0 } tools
                ? tools.Select(tool => new WireTool(new WireToolFunction(tool.Name, tool.Description, tool.Parameters))).ToList()
                : null,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            Stop = options.Stop,
        };
    }
}
