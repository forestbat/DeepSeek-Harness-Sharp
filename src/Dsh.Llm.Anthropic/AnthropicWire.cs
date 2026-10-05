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

        if (options.System is { } system)
            body["system"] = system;

        body["messages"] = SerializeMessages(options);

        if (options.Tools is { Count: > 0 } tools)
            body["tools"] = new JsonArray(tools.Select(SerializeTool).ToArray());

        if (options.ReasoningEffort is { } effort)
            body["output_config"] = new JsonObject { ["effort"] = effort.Value };

        return body.ToJsonString();
    }

    private static JsonArray SerializeMessages(GenerateOptions options)
    {
        var array = new JsonArray();
        foreach (var message in options.Messages)
            array.Add(SerializeMessage(options, message));
        return array;
    }

    private static JsonObject SerializeMessage(GenerateOptions options, Message message)
    {
        var role = message.Role switch
        {
            MessageRole.User => "user",
            MessageRole.Assistant => "assistant",
            _ => throw new LlmException(new LlmFailure(
                "Anthropic only accepts user and assistant messages in the messages array",
                "INVALID_REQUEST")),
        };

        var content = new JsonArray();
        foreach (var block in message.Content)
        {
            if (block is ReasoningBlock)
                continue;
            content.Add(SerializeBlock(options, block));
        }

        return new JsonObject
        {
            ["role"] = role,
            ["content"] = content,
        };
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
        ToolResultBlock result => SerializeToolResult(result),
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

    private static JsonObject SerializeToolResult(ToolResultBlock result)
    {
        var content = new JsonArray();
        foreach (var block in result.Content)
        {
            if (block is TextBlock text)
                content.Add(new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text.Text,
                });
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
