using Dsh.Core;
using Dsh.Llm;
using Message = Dsh.Llm.Message;

namespace Dsh.Agent;

/**
 * 一次请求投影的统计(验收 O1): 这次真正发给模型的那一份有多少条、多少字符、覆盖哪段事件号。
 *
 * 取值全部复用已经算好的东西 —— 请求消息集合、表明面的节点表、配对修补的计数;
 * 不为打点再扫一遍日志, 尤其不为它把窗口会话的前缀按页读回来。
 */
internal readonly record struct PromptRequestStats(
    int Messages,
    int Chars,
    long FirstSeq,
    long LastSeq,
    long CheckpointSeq,
    int Dropped)
{
    /** 取不到的事件号一律填这个值(O1 的约定, 与"序号为 0 的真实事件"区分开)。 */
    public const long UnknownSeq = -1;

    /**
     * 在请求投影构建完成之后取值。
     *
     * derived 是会话派生出来的完整消息序列(压缩/回退/修剪都已经折进去), request 是修补之后真正要发的那份,
     * pairing 是这次配对修补的计数。
     */
    public static PromptRequestStats Measure(
        Session session, IReadOnlyList<Message> derived, IReadOnlyList<Message> request, ToolCallPairing.Repaired pairing)
    {
        var nodes = session.SurfaceManager.Nodes;
        var (firstSeq, lastSeq) = NodeSeqRange(nodes);
        var chars = 0;
        foreach (var message in request)
            chars += BlocksChars(message.Content);
        // 因窗口缺页或内容为空没能变成请求消息的表面节点, 加上配对修补丢掉的孤立工具结果。
        var dropped = Math.Max(0, nodes.Count - derived.Count) + pairing.Dropped;
        return new PromptRequestStats(request.Count, chars, firstSeq, lastSeq, MaxCheckpointSeq(session, request), dropped);
    }

    private static (long First, long Last) NodeSeqRange(IReadOnlyList<long> nodes)
    {
        if (nodes.Count == 0)
            return (UnknownSeq, UnknownSeq);
        // 压缩会在表面中间插入新序号, 表面因此不再单调: 范围取全部节点号的最小/最大, 而不是首尾两项。
        var first = nodes[0];
        var last = nodes[0];
        for (var index = 1; index < nodes.Count; index += 1)
        {
            first = Math.Min(first, nodes[index]);
            last = Math.Max(last, nodes[index]);
        }
        return (first, last);
    }

    /** 消息里带了多少字符: 文本、思维链、工具名与参数、工具结果内容; 图片等二进制块不计。 */
    private static int BlocksChars(IReadOnlyList<ContentBlock> blocks)
    {
        var total = 0;
        foreach (var block in blocks)
        {
            total += block switch
            {
                TextBlock text => text.Text.Length,
                ReasoningBlock reasoning => reasoning.Text.Length,
                ToolCallBlock call => call.Name.Length + call.Arguments.Length,
                ToolResultBlock result => BlocksChars(result.Content),
                _ => 0,
            };
        }
        return total;
    }

    /**
     * 本次请求里压缩摘要/检查点事件的**最大序号**; 请求里没有检查点消息时一次都不碰日志。
     *
     * 两个来源: 常驻尾窗(压缩刚发生在尾巴上, 这里不读页)与表面折叠的 replace 记录
     * (每次压缩都把被覆盖的区间换成一个检查点节点, 那条记录带着检查点事件的序号)。
     * 检查点落在尾窗之前、而表面又来自 .surface 旁车(那份快照不带 replace 历史)时取不到, 填 -1。
     */
    private static long MaxCheckpointSeq(Session session, IReadOnlyList<Message> request)
    {
        if (!request.Any(IsCheckpointMessage))
            return UnknownSeq;
        var resident = session.LastResidentEventWhere(IsCheckpointEvent)?.Seq ?? UnknownSeq;
        var folded = UnknownSeq;
        foreach (var replacement in session.SurfaceManager.Replacements)
        {
            if (replacement.Seq > folded && IsCheckpointEvent(session.EventAt(replacement.Seq)))
                folded = replacement.Seq;
        }
        return Math.Max(resident, folded);
    }

    private static bool IsCheckpointMessage(Message message)
        => message.Source is PluginMessageSource { CompactionId: not null };

    private static bool IsCheckpointEvent(SessionEvent? sessionEvent) => sessionEvent?.Data switch
    {
        UserMessagePayload user => IsCheckpointMessage(user.Message),
        SystemMessagePayload system => IsCheckpointMessage(system.Message),
        _ => false,
    };
}
