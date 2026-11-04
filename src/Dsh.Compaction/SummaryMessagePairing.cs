using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Compaction;

/**
 * 把待摘要历史里的工具结构拍平成纯文本, 保证消息序列对任何 OpenAI 兼容 API 都合法。
 *
 * 为什么必须拍平: 会话表面上的配对是按「事件」算的(ToolPairing), 而摘要请求发出去的是按事件「派生」出来的
 * 消息。事件级平衡并不等于消息级合法 —— 只要有一个 tool 结果落在被保留的尾部、或某个结果被 SurfaceOp.Replace
 * 合并掉, 请求里就会出现「带 tool_calls 的 assistant 后面没有对应 tool 消息」, 服务端直接回
 * HTTP 400 invalid_request_error。拍平后不再有 tool 角色与 tool_calls 字段, 因此无论怎么切都不会触发配对校验。
 *
 * 信息不丢: 工具名、参数、输出、错误标记都保留为文本(Kilo Code 的 [Assistant tool call] / [Tool result] 做法),
 * 图片降级为占位文本, 因为摘要请求不接受图片内容。被中断的调用会补一行"无输出"占位, 免得模型误以为结果为空。
 */
public static class SummaryMessagePairing
{
    public const string ToolCallPrefix = "[tool call: ";
    public const string ToolResultPrefix = "[tool result: ";
    public const string ToolErrorMarker = " error]";
    public const string ToolInterruptedMarker = "(interrupted, no output)";
    public const string ImagePlaceholder = "[image omitted from compaction]";

    public static IReadOnlyList<Message> FlattenToolStructure(IReadOnlyList<Message> messages)
    {
        var answered = CollectAnsweredCallIds(messages);
        var flattened = new List<Message>(messages.Count);
        foreach (var message in messages)
        {
            if (message is ToolResultMessage toolResult)
            {
                flattened.Add(FlattenToolResult(toolResult));
                continue;
            }
            flattened.Add(FlattenContent(message, answered));
        }
        return flattened;
    }

    /** 区间内出现过结果的调用 id: 用来判断某个调用是否"有调用无结果"(被中断)。 */
    private static HashSet<string> CollectAnsweredCallIds(IReadOnlyList<Message> messages)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message is ToolResultMessage single)
                answered.Add(single.Block.ToolCallId.Value);
            foreach (var block in message.Content)
            {
                if (block is ToolResultBlock result)
                    answered.Add(result.ToolCallId.Value);
            }
        }
        return answered;
    }

    /** 只重写含工具调用/图片的消息; 其余原样返回, 避免无意义地重建历史。 */
    private static Message FlattenContent(Message message, HashSet<string> answered)
    {
        var blocks = new List<ContentBlock>(message.Content.Count);
        var changed = false;
        foreach (var block in message.Content)
        {
            switch (block)
            {
                case ToolCallBlock call:
                    blocks.Add(new TextBlock($"{ToolCallPrefix}{call.Name}] {call.Arguments}"));
                    if (!answered.Contains(call.Id.Value))
                        blocks.Add(new TextBlock($"{ToolResultPrefix}{call.Id.Value}] {ToolInterruptedMarker}"));
                    changed = true;
                    break;
                case ImageBlock:
                    blocks.Add(new TextBlock(ImagePlaceholder));
                    changed = true;
                    break;
                case ToolResultBlock result:
                    // 正常路径下工具结果在 ToolResultMessage 里; 这里兜底, 避免漏掉嵌套结构。
                    blocks.Add(new TextBlock(DescribeToolResult(result)));
                    changed = true;
                    break;
                default:
                    blocks.Add(block);
                    break;
            }
        }
        if (!changed)
            return message;
        return message switch
        {
            AssistantMessage assistant => assistant with { Content = blocks },
            _ => message with { Content = blocks },
        };
    }

    private static Message FlattenToolResult(ToolResultMessage toolResult)
        => new UserMessage
        {
            Id = MessageFactory.NewId(),
            Content = [new TextBlock(DescribeToolResult(toolResult.Block))],
            Source = toolResult.Source,
        };

    private static string DescribeToolResult(ToolResultBlock result)
    {
        var marker = result.IsError == true ? ToolErrorMarker : "]";
        var body = MessageText.Flatten(result.Content);
        return $"{ToolResultPrefix}{result.ToolCallId.Value}{marker} {body}";
    }
}
