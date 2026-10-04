namespace Dsh.Llm;

/**
 * 流式响应的看门狗阈值(毫秒): idle 每个 chunk 重置, lifetime 不重置; 非正值表示关闭该守卫。
 * 默认值对齐 qwen-code(4 分钟 / 15 分钟); C# 的 CancelAfter 接受整个非负 int 域, 无需 JS 那边的定时器上限保护。
 */
public sealed record LlmStreamLimits(int IdleTimeoutMs, int MaxLifetimeMs)
{
    public const int DefaultIdleTimeoutMs = 240_000;
    public const int DefaultMaxLifetimeMs = 900_000;

    public static readonly LlmStreamLimits Default = new(DefaultIdleTimeoutMs, DefaultMaxLifetimeMs);

    public bool Enabled => IdleTimeoutMs > 0 || MaxLifetimeMs > 0;

    public static LlmStreamLimits Resolve(int? idleTimeoutMs, int? maxLifetimeMs)
        => new(
            ResolveOne(idleTimeoutMs, DefaultIdleTimeoutMs),
            ResolveOne(maxLifetimeMs, DefaultMaxLifetimeMs));

    private static int ResolveOne(int? value, int fallback) => value switch
    {
        null => fallback,
        > 0 => value.Value,
        _ => 0,
    };
}
