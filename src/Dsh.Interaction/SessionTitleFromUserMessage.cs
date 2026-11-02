using Dsh.Llm;

namespace Dsh.Interaction;

/** 会话标题: 取首条用户消息的第一行, 缩短后写入 Session.Header.Title; 会话 id 保持不变。 */
public static class SessionTitleFromUserMessage
{
    public const int MaxTitleLength = 30;

    public static string? Derive(UserMessage message)
    {
        if (message.Source is not UserMessageSource)
            return null;
        var text = string.Concat(message.Content.OfType<TextBlock>().Select(block => block.Text));
        return Shorten(text);
    }

    private static string? Shorten(string text)
    {
        // 提示词侧注入的 <system-reminder> 不是用户写的内容, 不能变成会话标题。
        foreach (var line in UserFacingText.StripSystemReminders(text).Split('\n'))
        {
            var collapsed = string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (collapsed.Length == 0)
                continue;
            return collapsed.Length > MaxTitleLength ? collapsed[..MaxTitleLength] : collapsed;
        }
        return null;
    }
}
