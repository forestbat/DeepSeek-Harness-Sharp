using Dsh.Llm;
using LlmTextBlock = Dsh.Llm.TextBlock;

namespace Dsh.Gui.ViewModels;

/** 会话事件里的内容块到纯文本的提取: 主会话与子代理只读视图共用, 保证两处渲染口径一致。 */
internal static class SessionText
{
    public static string ContentText(IReadOnlyList<ContentBlock> blocks) => MessageText.Flatten(blocks);

    /** 思考内容已由流式增量渲染, 最终消息只取正文, 避免重复。 */
    public static string AssistantText(IReadOnlyList<ContentBlock> blocks)
        => string.Join('\n', blocks.OfType<LlmTextBlock>().Select(block => block.Text)).Trim();
}
