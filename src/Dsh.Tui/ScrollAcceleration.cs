using System.Runtime.InteropServices;

namespace Dsh.Tui;

/** 滚轮加速策略: 与 kilocode 的 OpenTUI 同款(linear 固定 1, macOS 式按事件间隔加速)。 */
internal interface IScrollAcceleration
{
    float Tick(long now);

    void Reset();
}

internal sealed class LinearScrollAcceleration : IScrollAcceleration
{
    public float Tick(long now) => 1f;

    public void Reset()
    {
    }
}

/**
 * macOS 式滚轮加速: 测量相邻事件间隔, 用最近几个间隔的均值决定倍率——快速连滚加速, 慢滚保持精确。
 * 阈值/曲线照抄 OpenTUI 的 scroll-acceleration.ts(A=0.8, tau=3, 上限 6 倍, 参考间隔 100ms)。
 */
internal sealed class MacOsScrollAcceleration : IScrollAcceleration
{
    private const int HistorySize = 3;
    private const long StreakTimeoutMs = 150;
    /** 有的终端(Ghostty)会把一格滚轮拆成多次 tick, 间隔约 4ms: 忽略这些 tick, 否则会误加速。 */
    private const long MinTickIntervalMs = 6;
    private const double ReferenceIntervalMs = 100;
    private const double CurveA = 0.8;
    private const double CurveTau = 3;
    private const float MaxMultiplier = 6f;

    private readonly List<long> _history = [];
    private long _lastTick;

    public float Tick(long now)
    {
        var delta = _lastTick == 0 ? long.MaxValue : now - _lastTick;
        if (delta == long.MaxValue || delta > StreakTimeoutMs)
        {
            _lastTick = now;
            _history.Clear();
            return 1f;
        }

        if (delta < MinTickIntervalMs)
            return 1f;

        _lastTick = now;
        _history.Add(delta);
        if (_history.Count > HistorySize)
            _history.RemoveAt(0);
        var average = 0d;
        foreach (var interval in _history)
            average += interval;
        average /= _history.Count;
        var velocity = ReferenceIntervalMs / average;
        var multiplier = 1 + (CurveA * (Math.Exp(velocity / CurveTau) - 1));
        return (float)Math.Min(multiplier, MaxMultiplier);
    }

    public void Reset()
    {
        _lastTick = 0;
        _history.Clear();
    }
}

/**
 * 每窗格一个: 事件增量(notches) × 每格基础行数 × 加速倍数 → 分数累积 → 取整滚动。
 * 分数累积保证高分辨率滚轮/触控板的半个 notch 不会被丢掉。
 */
internal sealed class ScrollWheel
{
    private readonly IScrollAcceleration _acceleration = new MacOsScrollAcceleration();
    private float _accumulator;

    public int Scroll(float notches)
    {
        if (notches == 0)
            return 0;
        var amount = notches * WheelLines.LinesPerNotch * _acceleration.Tick(Environment.TickCount64);
        _accumulator += amount;
        var whole = (int)MathF.Truncate(_accumulator);
        _accumulator -= whole;
        return whole;
    }
}

/** 每格滚轮的基础行数: Windows 读系统设置(一次滚几行), 其他平台为 1(与 OpenTUI 默认一致)。 */
internal static class WheelLines
{
    private const uint SpiGetWheelScrollLines = 0x0068;
    private const uint WheelPageScroll = 0xFFFFFFFF;
    /** 系统设成"整页"时按翻页处理, 与 PgUp/PgDn 的 10 行一致。 */
    private const int PageScrollLines = 10;

    public static int LinesPerNotch { get; } = ResolveLinesPerNotch();

    private static int ResolveLinesPerNotch()
        => OperatingSystem.IsWindows() ? WindowsLines() : 1;

    private static int WindowsLines()
    {
        try
        {
            if (SystemParametersInfo(SpiGetWheelScrollLines, 0, out var value, 0) && value != WheelPageScroll)
                return (int)Math.Clamp(value, 1u, PageScrollLines);
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, out uint value, uint winIni);
}
