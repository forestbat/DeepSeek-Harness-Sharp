using Dsh.Core;

namespace Dsh.Tui;

/**
 * 打开会话的打点(对应 GUI 的 SessionOpenProbe): 视口/首帧/可交互三个刻度 + 观测窗口内最长的帧间隔。
 *
 * 为什么要有它: "打开会话慢"在 TUI 里没有数字 —— 用户只感到"卡一下", 事后没法判断是回放、补页还是
 * 第一帧渲染贵。这里用渲染循环自己的节拍量"相邻帧间隔"(等价于 GUI 的 DispatcherTimer 节拍),
 * 不需要额外线程, 也不会因为终端空闲而把"没人按键"算成卡顿: 只在打开后的观测窗口内计时。
 */
internal sealed class TuiOpenProbe(string sessionId)
{
    /** 观测窗口: 与 GUI 同一口径(20s)。窗口外的空闲阻塞不算 stall。 */
    public const long ObservationWindowMs = 20_000;

    private readonly long _startedAt = Environment.TickCount64;
    private long _lastFrameAt = Environment.TickCount64;
    private long _viewportMs = -1;
    private long _firstPaintMs = -1;
    private long _interactiveMs = -1;
    private long _maxStallMs;

    public string SessionId => sessionId;

    /** 已经写过的那一行(只为"只报一次"服务)。 */
    public string? Reported { get; private set; }

    public bool WindowOpen => Environment.TickCount64 - _startedAt <= ObservationWindowMs;

    /** 三个刻度都到齐: 可以出日志了。 */
    public bool Ready => _viewportMs >= 0 && _firstPaintMs >= 0 && _interactiveMs >= 0;

    public void MarkViewport() => _viewportMs = Mark(_viewportMs);

    public void MarkFirstPaint() => _firstPaintMs = Mark(_firstPaintMs);

    public void MarkInteractive() => _interactiveMs = Mark(_interactiveMs);

    /** 每帧调一次: 只在窗口内累计"相邻帧间隔的最大值"。 */
    public void OnFrame()
    {
        var now = Environment.TickCount64;
        var gap = now - _lastFrameAt;
        _lastFrameAt = now;
        if (now - _startedAt <= ObservationWindowMs)
            _maxStallMs = Math.Max(_maxStallMs, gap);
    }

    /** 供日志的一行: 行为与开关无关, 只多一行观测。 */
    public string Describe(Session session)
    {
        Reported = $"tui open: id={sessionId} viewport_ms={_viewportMs} firstPaint_ms={_firstPaintMs}"
            + $" interactive_ms={_interactiveMs} stall_ms={_maxStallMs} events={session.Seq}"
            + $" residentFrom={session.ResidentFrom} page_faults={session.PageFaults}"
            + $" page_distinct={session.PageFaultDistinct}";
        return Reported;
    }

    private long Mark(long current) => current >= 0 ? current : Environment.TickCount64 - _startedAt;
}
