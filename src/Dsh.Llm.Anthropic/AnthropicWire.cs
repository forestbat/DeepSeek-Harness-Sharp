using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Llm.Anthropic;

internal static class AnthropicWire
{
    public const int DefaultMaxTokens = 4096;

    public static string SerializeRequest(GenerateOptions options)
    {
        var body = new JsonObject
        {
            ["model"] = options.Model,
            ["max_tokens"] = options.MaxTokens ?? DefaultMaxTokens,
            ["stream"] = true,
        };

        var system = SerializeSystem(options);
        if (system.Count > 0)
        {
            MarkCacheBreakpoint((JsonObject)system[^1]!);
            body["system"] = system;
        }

        var messages = SerializeMessages(options);
        MarkLastBlockCacheBreakpoint(messages);
        body["messages"] = messages;

        if (options.Tools is { Count: > 0 } tools)
            body["tools"] = new JsonArray(tools.Select(SerializeTool).ToArray());

        if (options.ReasoningEffort is { } effort)
            body["output_config"] = new JsonObject { ["effort"] = effort.Value };

        return body.ToJsonString();
    }

    /**
     * Anthropic 只在显式 cache_control 断点处建立前缀缓存, 不打点则每次请求都是全量输入。
     * 断点打在系统提示与最后一条消息的最后一个块上: 下一次请求的前缀到旧断点为止都能命中。
     */
    private static void MarkCacheBreakpoint(JsonObject block)
        => block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

    private static void MarkLastBlockCacheBreakpoint(JsonArray messages)
    {
        if (messages.Count == 0
            || messages[^1] is not JsonObject last
            || last["content"] is not JsonArray content
            || content.Count == 0
            || content[^1] is not JsonObject block)
        {
            return;
        }
        MarkCacheBreakpoint(block);
    }

    /** Anthropic 只在顶层 system 字段接收系统提示: options.System 在前, 消息里的 system 角色按出现顺序合并。 */
    private static JsonArray SerializeSystem(GenerateOptions options)
    {
        var texts = new List<string>();
        if (options.System is { } system)
            texts.Add(system);
        foreach (var block in options.Messages.Where(message => message.Role == MessageRole.System).SelectMany(message => message.Content))
        {
            texts.Add(block is TextBlock text
                ? text.Text
                : throw new LlmException(new LlmFailure(
                    $"Anthropic system prompt does not support content block \"{block.Type}\"",
                    "UNSUPPORTED_CONTENT")));
        }
        // 空文本块会被 API 拒绝。
        return new JsonArray([.. texts
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = text })]);
    }

    private static JsonArray SerializeMessages(GenerateOptions options)
        => new([.. options.Messages
            .Where(message => message.Role != MessageRole.System)
            .Select(message => (JsonNode)SerializeMessage(options, message))]);

    private static JsonObject SerializeMessage(GenerateOptions options, Message message)
    {
        var role = message.Role == MessageRole.Assistant ? "assistant" : "user";
        var thinking = ThinkingReplayFor(message);
        var reasoningSeen = 0;
        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            if (block is not ReasoningBlock)
                content.Add(SerializeBlock(options, block));
            else if (thinking is not null)
                content.Add(thinking[reasoningSeen++].DeepClone());
        }

        return new JsonObject
        {
            ["role"] = role,
            ["content"] = content,
        };
    }

    /**
     * 带签名的原始 thinking 块, 与消息里的 ReasoningBlock 一一对应。
     * 工具调用轮次必须原样回放上一条 assistant 的 thinking 块; 没有本适配器的回放状态(旧消息、其他供应商)或数量对不上时只能丢弃推理。
     */
    private static IReadOnlyList<JsonNode>? ThinkingReplayFor(Message message)
    {
        var replay = AnthropicThinkingReplay.Decode(message);
        return replay?.Count == message.Content.Count(block => block is ReasoningBlock) ? replay : null;
    }

    private static JsonObject SerializeBlock(GenerateOptions options, ContentBlock block) => block switch
    {
        TextBlock text => new JsonObject
        {
            ["type"] = "text",
            ["text"] = text.Text,
        },
        ToolCallBlock call => new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = call.Id.Value,
            ["name"] = call.Name,
            ["input"] = ParseArguments(call.Arguments),
        },
        ToolResultBlock result => SerializeToolResult(options, result),
        ImageBlock image => new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = image.Attachment.MediaType,
                ["data"] = Convert.ToBase64String(AttachmentResolution.Resolve(options, image.Attachment).Span),
            },
        },
        _ => throw new LlmException(new LlmFailure(
            $"Anthropic adapter does not support content block \"{block.Type}\"",
            "UNSUPPORTED_CONTENT")),
    };

    /** tool_result 的 content 原生接受 text 与 image 块。 */
    private static JsonObject SerializeToolResult(GenerateOptions options, ToolResultBlock result)
    {
        var content = new JsonArray();
        foreach (var block in result.Content)
        {
            if (block is TextBlock or ImageBlock)
                content.Add(SerializeBlock(options, block));
            else
                throw new LlmException(new LlmFailure(
                    $"Anthropic tool result does not support content block \"{block.Type}\"",
                    "UNSUPPORTED_CONTENT"));
        }

        if (content.Count == 0)
            content.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = "",
            });

        var obj = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = result.ToolCallId.Value,
            ["content"] = content,
        };

        if (result.IsError is { } isError)
            obj["is_error"] = isError;

        return obj;
    }

    private static JsonObject SerializeTool(ToolSchema tool) => new()
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["input_schema"] = tool.Parameters,
    };

    private static JsonNode ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(arguments) ?? new JsonObject();
        }
        catch (JsonException)
        {
            throw new LlmException(new LlmFailure(
                "Anthropic tool call arguments are not valid JSON",
                "INVALID_REQUEST"));
        }
    }
}
