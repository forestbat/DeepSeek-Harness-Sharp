using Dsh.Core;
using Dsh.Llm;

namespace Dsh.Interaction;

/**
 * 上下文整理(压缩)进度的中立视图。
 *
 * 为什么放在交互层而不是产生它的插件里: 压缩是插件(Dsh.Compaction), 而 TUI/GUI 只是终端消费者。
 * 让 TUI 为了几行进度文案硬引用压缩插件, 会把"界面"和"某个可选插件"绑死 —— 插件没装时界面编不过, 插件改名时界面要跟着改。
 * 这里的记录类型只描述"用户需要看到什么", 不携带任何压缩实现细节(选区、摘要模型等都不出现)。
 */
public abstract record ContextMaintenanceEvent
{
    /** 整理开始: 期间应显示进行中状态, 否则用户会以为界面卡死并重复触发。 */
    public sealed record Started : ContextMaintenanceEvent;

    /** 整理前裁剪了过大的工具结果。 */
    public sealed record Pruned(int Items, int Tokens) : ContextMaintenanceEvent;

    /** 整理产出了摘要; Summary 已是可直接显示的纯文本。 */
    public sealed record Summarized(int Items, int Tokens, string Summary) : ContextMaintenanceEvent;

    /** 整理失败。 */
    public sealed record Failed(string Error) : ContextMaintenanceEvent;

    /** 整理正常结束。 */
    public sealed record Finished : ContextMaintenanceEvent;
}

/**
 * 把"上下文整理"事件翻译成中立视图的接缝。
 * 由提供该能力的插件实现并注册; 界面侧只用这个接口, 因此可以在没有该插件的情况下正常编译与运行(取不到实现即可)。
 */
public interface IContextMaintenancePresenter
{
    public const string ServiceName = "contextMaintenance";

    /** 该事件不属于上下文整理时返回 null, 调用方按普通事件处理。 */
    ContextMaintenanceEvent? Describe(SessionEvent sessionEvent);

    /** 这条消息是不是整理产生的检查点: 其正文已由摘要事件渲染过, 不应再当作上下文注入重复一遍。 */
    bool IsCheckpointMessage(MessageSource source);
}
