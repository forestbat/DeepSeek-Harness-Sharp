using System.Text;

namespace Dsh.Interaction;

/** 会话界面展示用的文本清洗: 移除只给模型看的注入块, 不改动会话事件本身。 */
public static class UserFacingText
{
    private const string OpenTag = "<system-reminder>";
    private const string CloseTag = "</system-reminder>";

    /** 去掉所有 system-reminder 块(含未闭合到末尾的), 其余文本保留。 */
    public static string StripSystemReminders(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase) < 0)
            return text;
        var builder = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var open = text.IndexOf(OpenTag, position, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                builder.Append(text, position, text.Length - position);
                break;
            }
            builder.Append(text, position, open - position);
            var contentStart = open + OpenTag.Length;
            var close = text.IndexOf(CloseTag, contentStart, StringComparison.OrdinalIgnoreCase);
            position = close < 0 ? text.Length : close + CloseTag.Length;
        }
        return builder.ToString().Trim();
    }
}
