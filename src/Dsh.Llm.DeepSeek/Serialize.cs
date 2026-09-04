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

    private static void AssertTextOnly(IReadOnlyList<ContentBlock> blocks)
    {
        if (blocks.Any(block => block is ImageBlock))
        {
            throw new LlmException(new LlmFailure(
                "The DeepSeek chat-completions adapter does not support image content.",
                "UNSUPPORTED_CONTENT"));
        }
    }

    private static WireMessage SerializeAssistant(Message message)
    {
        var text = FlattenText(message.Content);
        var reasoning = string.Concat(message.Content.OfType<ReasoningBlock>().Select(block => block.Text));
        var toolCalls = message.Content
            .OfType<ToolCallBlock>()
            .Select(block => new WireToolCall(block.Id.Value, block.Name, block.Arguments))
            .ToList();
        return new WireMessage.Assistant(
            text,
            reasoning.Length > 0 ? reasoning : null,
            toolCalls.Count > 0 ? toolCalls : null);
    }

    public static IReadOnlyList<WireMessage> SerializeMessages(IReadOnlyList<Message> messages)
    {
        var wire = new List<WireMessage>();
        foreach (var message in messages)
        {
            AssertTextOnly(message.Content);
            switch (message.Role)
            {
                case MessageRole.System:
                    wire.Add(new WireMessage.System(FlattenText(message.Content)));
                    continue;
                case MessageRole.Assistant:
                    wire.Add(SerializeAssistant(message));
                    continue;
                default:
                {
                    var toolResults = message.Content.OfType<ToolResultBlock>().ToList();
                    var text = FlattenText(message.Content);
                    if (text.Length > 0 || toolResults.Count == 0)
                        wire.Add(new WireMessage.User(text));
                    foreach (var result in toolResults)
                    {
                        var content = FlattenText(result.Content);
                        wire.Add(new WireMessage.Tool(result.ToolCallId, content.Length > 0 ? content : "(no output)"));
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
            messages.Add(new WireMessage.System(system));
        messages.AddRange(SerializeMessages(options.Messages));
        var (thinking, reasoningEffort) = ResolveThinking(options, defaults);
        return new WireRequest
        {
            Model = options.Model,
            Messages = messages,
            Thinking = thinking is null ? null : new JsonObject { ["type"] = thinking },
            ReasoningEffort = reasoningEffort,
            Tools = options.Tools is { Count: > 0 } tools
                ? tools.Select(tool => new WireTool(tool.Name, tool.Description, tool.Parameters)).ToList()
                : null,
            Temperature = options.Temperature,
            MaxTokens = options.MaxTokens,
            Stop = options.Stop,
        };
    }
}
