using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Agent;

/**
 * 请求投影上的工具调用配对不变量: assistant 的每个 tool_calls 后面必须跟齐对应 call id 的 tool 结果。
 *
 * 会话历史可能缺一段(停写的尾巴、取不回的页、回退或压缩切在配对中间), 而供应商对
 * "assistant 带 tool_calls 却没有 tool 消息"是直接拒绝请求的(实测 deepseek-ly: INVALID_REQUEST)。
 * 所以这里在**发请求之前**把投影补齐: 缺的补一条"结果未知"、孤立的 tool 结果丢掉。
 *
 * 只改这一次请求的消息列表, 不改会话日志, 也不改 surface: 日志里那条悬空调用该怎么记还是怎么记。
 */
internal static class ToolCallPairing
{
    /** Patched = 补出来的"结果未知"工具结果条数; Dropped = 丢掉的孤立工具结果条数(都没有则消息原样返回)。 */
    internal readonly record struct Repaired(IReadOnlyList<Message> Messages, int Patched, int Dropped);

    public static Repaired Repair(IReadOnlyList<Message> messages)
    {
        var output = new List<Message>(messages.Count);
        var pending = new List<ToolCallId>();
        var patched = 0;
        var dropped = 0;
        foreach (var message in messages)
        {
            var results = message.Content.OfType<ToolResultBlock>().ToList();
            if (results.Count > 0)
            {
                var kept = KeepMatched(message, results, pending, ref dropped);
                if (kept is not null)
                    output.Add(kept);
                continue;
            }
            patched += FlushPending(output, pending);
            output.Add(message);
            foreach (var call in message.Content.OfType<ToolCallBlock>())
            {
                if (!pending.Contains(call.Id))
                    pending.Add(call.Id);
            }
        }
        patched += FlushPending(output, pending);
        return new Repaired(output, patched, dropped);
    }

    /** 只留"前面确实有对应 tool_calls"的结果块; 一条都不剩就整条消息丢掉。 */
    private static Message? KeepMatched(Message message, List<ToolResultBlock> results, List<ToolCallId> pending, ref int dropped)
    {
        var kept = new List<ContentBlock>(results.Count);
        foreach (var result in results)
        {
            if (pending.Remove(result.ToolCallId))
                kept.Add(result);
            else
                dropped += 1;
        }
        if (kept.Count == 0)
            return null;
        return kept.Count == results.Count ? message : message with { Content = kept };
    }

    /**
     * 把还欠着的调用补成"结果未知"的工具结果, 一条消息一个结果(与正常落盘的形状一致)。
     *
     * 文本复用 `SessionRepair.OutcomeUnknownText`: 同一句话既用于恢复时的收尾事件, 也用于这里的请求补齐,
     * 模型看到的语义才不会因为"补在哪一层"而变。
     */
    private static int FlushPending(List<Message> output, List<ToolCallId> pending)
    {
        if (pending.Count == 0)
            return 0;
        foreach (var callId in pending)
            output.Add(MessageFactory.CreateToolResultMessage(callId, [new TextBlock(SessionRepair.OutcomeUnknownText)], isError: true));
        var count = pending.Count;
        pending.Clear();
        return count;
    }
}
