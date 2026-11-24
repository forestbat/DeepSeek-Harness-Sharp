using Dsh.Llm;

namespace Dsh.Agent;

/**
 * 打开会话的一次汇总打点(方案 §7 M6): 一行覆盖"会话可读 → 用户看到内容 → 可以输入"。
 *
 * 只记当前这一层真拿得到的时刻: TUI 直连与 GUI 的宿主服务都在 AgentRegistry.Resume 里打开会话,
 * 所以 open/interactive 在这里量; 首次可见(firstPaint)与主线程最长阻塞(stall)发生在各前端进程内,
 * 这一层拿不到就填 -1 —— 用估算值顶替等于把指标变成噪声。
 */
public readonly record struct SessionOpenReport(
    SessionId Id,
    double OpenMs,
    double FirstPaintMs,
    double InteractiveMs,
    double StallMs,
    long Events,
    bool Windowed)
{
    /** 当前这一层拿不到的毫秒数: 填 -1 而不是 0, 0 会被读成"没花时间"。 */
    public const double UnknownMs = -1;

    public string Line =>
        $"session open: id={Id.Value} open_ms={Milliseconds(OpenMs)} firstPaint_ms={Milliseconds(FirstPaintMs)} "
        + $"interactive_ms={Milliseconds(InteractiveMs)} stall_ms={Milliseconds(StallMs)} events={Events} windowed={Windowed}";

    /** 整数毫秒: 不经过区域格式, 这一行要能被脚本按空白切分。 */
    private static long Milliseconds(double value)
        => value < 0 ? -1 : (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
