using Dsh.Llm;

namespace Dsh.Interaction;

/**
 * 会话的上下文占用: 最近一次请求实际发出的提示词 token 数, 对照当前模型的窗口容量。
 * GUI 状态栏与 TUI 状态行共用这一份取数与文案, 避免两边各写一套阈值和格式化。
 * 注意取"最近一次请求"而不是累计值: 上下文窗口被占用的是当前提示词, 与历史累计无关。
 */
public sealed class ContextUsage
{
    public const string Empty = "—";

    /** 当前模型的窗口容量; 未知(未配置 contextWindow)时为 null。 */
    public int? ContextWindow { get; private set; }

    /** 最近一次请求的提示词 token 数; 尚未收到用量时为 0。 */
    public double LastInputTokens { get; private set; }

    /** 一次新请求的上下文元数据到达(模型可能切换, 容量随之变化)。 */
    public void ObserveRequest(int? contextWindow)
        => ContextWindow = contextWindow is > 0 ? contextWindow : null;

    /** 用一次请求的用量刷新占用; 只有输入 token 才代表上下文占用。 */
    public void ObserveUsage(TokenUsage usage)
    {
        if (usage.InputTokens > 0)
            LastInputTokens = usage.InputTokens;
        else if (usage.TotalTokens is > 0)
            LastInputTokens = usage.TotalTokens.Value;
    }

    public void Reset()
    {
        ContextWindow = null;
        LastInputTokens = 0;
    }

    /** 状态行文案: 有容量时带百分比, 无容量时只报占用, 完全没有数据时显示占位符。 */
    public string Label => LastInputTokens > 0
        ? ContextWindow is { } window
            ? $"上下文 {TokenText.Compact(LastInputTokens)}/{TokenText.Compact(window)} ({LastInputTokens / window * 100:0}%)"
            : $"上下文 {TokenText.Compact(LastInputTokens)}"
        : Empty;
}

/** token 数量的紧凑写法(GUI 累计统计与上下文占用共用)。 */
public static class TokenText
{
    public static string Compact(double value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000:0.0}M",
        >= 1_000 => $"{value / 1_000:0.0}k",
        _ => value.ToString("0"),
    };
}
