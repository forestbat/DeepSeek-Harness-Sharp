using System.Diagnostics;
using Avalonia.Threading;

namespace Dsh.Gui.Services;

/** 一次打开的可感知代价(方案 §6.3 的四个刻度), 单位毫秒; -1 表示这一项没量到。 */
public readonly record struct OpenCostResult(long ViewportMs, long FirstPaintMs, long InteractiveMs, long StallMs, long TotalMs, long Events);

/**
 * 打开会话的三个用户体感刻度(方案 §6.3 / M6): 第一屏内容、可输入、以及期间主线程最长阻塞。
 *
 * 这几件事只发生在 GUI 进程里: 宿主只知道"会话可读", 看不到帧。所以在视图模型开一个探针,
 * 用**调度器回调的时间间隔**量主线程阻塞(一次长同步任务会让相邻回调之间的间隔变大), 而不是采样式估算。
 *
 * 三个值都允许未知(-1): 打开被打断、或没走到那一步时, 宁可报 -1 也不要报一个看起来合理的假数。
 */
public sealed class SessionOpenProbe
{
    /** 一次打开的最长观察窗口: 超过它就不再计 stall, 免得把用户后续操作算进"打开"。 */
    private static readonly TimeSpan ObservationWindow = TimeSpan.FromSeconds(20);

    /** 点击到探针建立之间已经过去的时间(宿主读日志 + 分页传到客户端): 那几个刻度必须把它算进去。 */
    private readonly long _alreadyElapsedMs;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _ticker;
    private long _lastTickAt;
    private double _viewport = Unknown;
    private double _firstPaint = Unknown;
    private double _interactive = Unknown;
    private double _stall;
    private bool _finished;

    private const double Unknown = -1;
    private const long TickIntervalMs = 16;

    /** startOffsetMs: 点击会话到此刻已经过去多少毫秒(打开主流程之外的那一段不算进来就永远是假数)。 */
    public SessionOpenProbe(long startOffsetMs = 0)
    {
        _alreadyElapsedMs = Math.Max(0, startOffsetMs);
        _lastTickAt = _clock.ElapsedMilliseconds;
        _ticker = new DispatcherTimer(TimeSpan.FromMilliseconds(TickIntervalMs), DispatcherPriority.Background, OnTick);
        _ticker.Start();
    }

    /**
     * 首个界面帧的时间(毫秒): 内容已经进视图模型之后, 排到渲染优先级上再记 ——
     * 那时布局与绘制已经轮过一轮, 比"赋值那一刻"更接近用户真正看到内容的时刻。
     */
    public void MarkFirstPaint()
        => Dispatcher.UIThread.Post(() =>
        {
            if (_firstPaint < 0)
                _firstPaint = _alreadyElapsedMs + _clock.ElapsedMilliseconds;
        }, DispatcherPriority.Render);

    /**
     * 会话视口出现的时间(毫秒): 点击之后界面切到这条会话、用户看到"已经进来了"的那一刻。
     *
     * 与 `firstPaint`(整段历史都投影完) 分开: 用户说"打开用了 5 秒"量的是这一个,
     * 而百万条会话把历史铺完还要更久 —— 两个数混在一起就永远对不上秒表。
     */
    public void MarkViewport() => _viewport = _viewport < 0 ? _alreadyElapsedMs + _clock.ElapsedMilliseconds : _viewport;

    /** 可输入的时间(毫秒): 回放结束、输入区可用。 */
    public void MarkInteractive() => _interactive = _interactive < 0 ? _alreadyElapsedMs + _clock.ElapsedMilliseconds : _interactive;

    /** 结构化结果(验收/测试断言用): 与 Finish 返回的那一行是同一批数字。 */
    public OpenCostResult Snapshot(double totalMs, long events)
        => new(
            ViewportMs: _viewport < 0 ? -1 : (long)Math.Round(_viewport, MidpointRounding.AwayFromZero),
            FirstPaintMs: _firstPaint < 0 ? -1 : (long)Math.Round(_firstPaint, MidpointRounding.AwayFromZero),
            InteractiveMs: _interactive < 0 ? -1 : (long)Math.Round(_interactive, MidpointRounding.AwayFromZero),
            StallMs: _stall > 0 ? (long)Math.Round(_stall, MidpointRounding.AwayFromZero) : -1,
            TotalMs: (long)Math.Round(totalMs, MidpointRounding.AwayFromZero),
            Events: events);

    /** 收尾: 返回一行汇总; `totalMs` 由调用方给(它才知道后台任务何时都结束)。 */
    public string Finish(string sessionId, double totalMs, long events)
    {
        if (_finished)
            return "";
        _finished = true;
        _ticker.Stop();
        var stall = _stall > 0 ? _stall : Unknown;
        return $"session paint: id={sessionId} viewport_ms={Ms(_viewport)} firstPaint_ms={Ms(_firstPaint)} interactive_ms={Ms(_interactive)} "
            + $"stall_ms={Ms(stall)} total_ms={Ms(totalMs)} events={events}";
    }

    public double ElapsedMs => _alreadyElapsedMs + _clock.ElapsedMilliseconds;

    public void Stop()
    {
        _finished = true;
        _ticker.Stop();
    }

    private void OnTick(object? sender, EventArgs args)
    {
        var now = _clock.ElapsedMilliseconds;
        var gap = now - _lastTickAt;
        _lastTickAt = now;
        if (gap > _stall)
            _stall = gap;
        if (_finished || _clock.Elapsed > ObservationWindow)
            _ticker.Stop();
    }

    private static long Ms(double value) => value < 0 ? -1 : (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
