using Dsh.Llm;
using Dsh.Core;

namespace Dsh.Agent;

/**
 * 后台运行的完成通知: 一条投回父会话的 plugin 来源消息。
 *
 * 为什么要单独标记: 用户在主会话按停会清空收件箱, 而"后台任务已经跑完"正是按停之后最需要看到的消息。
 * 带标记的消息在取消时被保留, 否则成果会随按停一起消失。
 */
public static class BackgroundResultNotice
{
    public const string PluginName = "subagent";
    public const string Form = "background-result";

    public static UserMessage Create(string text)
        => MessageFactory.CreateUserText(text, new PluginMessageSource(PluginName, Form));

    /** 取消清箱时应当保留的"后台结果"通知。 */
    public static bool IsNotice(UserMessage message)
        => message.Source is PluginMessageSource { Plugin: PluginName, Form: Form };
}
