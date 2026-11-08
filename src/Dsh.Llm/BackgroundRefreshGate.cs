namespace Dsh.Llm;

/**
 * 后台拉取的节流门: 冷却窗口内的重复请求合并成一次, 拉取进行中不重入。
 * 端点抖动(连不上、超时)时清单会一直为空, 于是每次重算模型选单都有一点机会把它补上, 而不是必须重启进程。
 */
public sealed class BackgroundRefreshGate(TimeSpan cooldown)
{
    private long _lastAttemptMilliseconds;
    private int _busy;

    /** 允许本次拉取时返回 true 并占用闸门; 调用方必须配对调用 End()。 */
    public bool TryBegin()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return false;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastAttemptMilliseconds);
        if (last != 0 && now - last < (long)cooldown.TotalMilliseconds)
        {
            Volatile.Write(ref _busy, 0);
            return false;
        }
        Interlocked.Exchange(ref _lastAttemptMilliseconds, now);
        return true;
    }

    /** 释放闸门: 无论拉取成功与否都要调用, 否则后续请求会被永久吞掉。 */
    public void End() => Volatile.Write(ref _busy, 0);
}
