namespace Dsh.Gui.ViewModels;

/**
 * 一条会话的阅读位置: 上次停在哪个像素, 以及当时视口顶部那条消息的身份。
 *
 * 像素只对同一份内容有意义: 整段重建(切会话/重新加载/重同步)会换掉消息对象、改变文档高度,
 * 于是"回到 12000px"可能落到别的内容上, 甚至落不进新文档。所以还原之前先问一句
 * "当时读的那条还在不在"(见 IsStillHere): 还在才按那个像素回去, 不在就贴最新。
 */
public readonly record struct TranscriptAnchor(double Offset, long? Seq, MessageKind Kind, string Text)
{
    /**
     * 锚点那条消息是否还在当前的转录里。
     * 有事件序号的(用户消息/时间线条目)按序号找, 没序号的按"种类 + 正文"找: 重建会换对象, 只能比这些。
     */
    public bool IsStillHere(IEnumerable<MessageViewModel> messages)
    {
        foreach (var message in messages)
        {
            if (message.Kind != Kind)
                continue;
            if (Seq is { } seq ? message.Seq == seq : string.Equals(message.Text, Text, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
