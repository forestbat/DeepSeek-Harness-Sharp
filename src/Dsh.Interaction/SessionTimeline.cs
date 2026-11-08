using System.Text;
using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Interaction;

/**
 * 会话时间线: 用户自己发的消息(序号 / 事件时间 / 文本)。
 *
 * TUI 的 `/timestamp` 与 GUI 的时间跳转必须给出同一份候选, 否则两边对"哪些事件算用户消息"
 * 的判定会各写一遍并慢慢分叉, 所以过滤规则收在这里(它依赖 UserFacingText 的展示清洗,
 * 因此和 UserFacingText 同属 Interaction)。
 */
public static class SessionTimeline
{
    /** 时间线的一项: 事件序号、事件时间(epoch 毫秒)、剥离系统提醒后的用户文本。 */
    public readonly record struct Entry(long Seq, long Time, string Text);

    /** 时间线预览的最大字符数; 超出部分用 ~ 结尾, 让选单行保持一行宽。 */
    public const int PreviewCharacters = 60;

    public static IReadOnlyList<Entry> UserMessages(Session session) => FromEvents(session.SnapshotEvents());

    public static IReadOnlyList<Entry> FromEvents(IReadOnlyList<SessionEvent> events)
    {
        var entries = new List<Entry>();
        foreach (var sessionEvent in events)
        {
            var text = UserText(sessionEvent);
            if (text.Length > 0)
                entries.Add(new Entry(sessionEvent.Seq, sessionEvent.Time, text));
        }
        return entries;
    }

    /** 用户自己发的消息才算数: 插件/工具投回来的消息不属于"我说过的话"。 */
    public static string UserText(SessionEvent sessionEvent)
    {
        if (sessionEvent.Data is not UserMessagePayload user || user.Message.Source is not UserMessageSource)
            return "";
        return UserFacingText.StripSystemReminders(TextOf(user.Message));
    }

    public static string TextOf(UserMessage message)
    {
        var text = new StringBuilder();
        foreach (var block in message.Content)
            if (block is TextBlock blockText)
                text.Append(blockText.Text);
        return text.ToString();
    }

    /**
     * 这条消息所属回合的 TurnStart 序号(找不到就是 0)。
     * 回退与分叉都必须停在这里而不是消息本身: 事件顺序是 TurnStart → UserMessage, 砍在消息处会留下
     * 一个没有 TurnEnd 的回合, 界面重放就永远停在"正在运行"。
     */
    public static long TurnStartOf(IReadOnlyList<SessionEvent> events, long seq)
    {
        for (var index = events.Count - 1; index >= 0; index--)
        {
            if (events[index].Seq > seq)
                continue;
            if (events[index].Data is TurnStartPayload)
                return events[index].Seq;
        }
        return 0;
    }

    /** 时间 ≤ 目标时刻的最近一条用户消息(消息按时间递增, 取最后一个满足的即可)。 */
    public static long? NearestSeq(IReadOnlyList<Entry> entries, long timestamp)
    {
        long? nearest = null;
        foreach (var entry in entries)
            if (entry.Time <= timestamp)
                nearest = entry.Seq;
        return nearest;
    }

    /** 选单里的一行预览: 折成单行, 超长截断。 */
    public static string Preview(string text, int limit = PreviewCharacters)
    {
        var single = text.Replace('\n', ' ');
        return single.Length <= limit ? single : $"{single[..limit]}~";
    }
}
