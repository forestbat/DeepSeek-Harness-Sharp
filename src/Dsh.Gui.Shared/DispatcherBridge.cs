using System.Diagnostics;
using Avalonia.Threading;

namespace Dsh.Gui.Services;

/**
 * 后台线程产生的事件先入队, 合并后分批投递到 UI 线程。
 *
 * 一条一次 Post 会把百万级事件变成百万个 dispatcher job; 一个 job 里把所有积压一次干完又会把 UI 线程按在
 * 秒级(长会话补历史实测单次连续占用 1,750ms)。所以 drain 有帧预算: 干满预算就让出一帧, 用 Background 优先级
 * 重新投递(Input 优先级的点击/滚动排在这一帧之前), 期间到达的事件并进下一批。批大小按上一批的实测速度反推,
 * 于是每批都正好是"这一帧装得下的量", 不再是 1 条一批。
 */
public sealed class DispatcherBridge<T>(Action<IReadOnlyList<T>> drain)
{
    /** 一帧留给 drain 的毫秒数: 16.6ms 的帧里给 UI 本身留一半。 */
    private const double FrameBudgetMs = 8;

    private const int MaxBatch = 4096;

    private readonly object _gate = new();
    private readonly List<T> _pending = [];
    private bool _scheduled;

    /** 下一批打算取几条: 由上一批的实测耗时反推, 让每批大约用满一帧预算。 */
    private int _batch = 1;

    public void Enqueue(T item)
    {
        lock (_gate)
        {
            _pending.Add(item);
            if (_scheduled)
                return;
            _scheduled = true;
        }
        Dispatcher.UIThread.Post(Drain);
    }

    private void Drain()
    {
        var clock = Stopwatch.StartNew();
        var yielding = false;
        try
        {
            while (true)
            {
                T[] batch;
                lock (_gate)
                {
                    if (_pending.Count == 0)
                        return;
                    batch = TakeLocked();
                }
                var started = clock.Elapsed;
                drain(batch);
                Recalibrate(batch.Length, clock.Elapsed - started);
                if (clock.Elapsed.TotalMilliseconds < FrameBudgetMs)
                    continue;
                // 预算用完: 还有积压就带着投递权让出一帧, 期间新到的事件并进下一批而不是再投一个 job。
                lock (_gate)
                {
                    if (_pending.Count == 0)
                        return;
                    Dispatcher.UIThread.Post(Drain, DispatcherPriority.Background);
                    yielding = true;
                }
                return;
            }
        }
        finally
        {
            // 让出时投递权留在下一次 Drain 手里; 排空或抛出才放开, 否则后续事件再也进不来。
            if (!yielding)
            {
                lock (_gate)
                    _scheduled = false;
            }
        }
    }

    private T[] TakeLocked()
    {
        var take = Math.Min(_pending.Count, _batch);
        var batch = new T[take];
        _pending.CopyTo(0, batch, 0, take);
        _pending.RemoveRange(0, take);
        return batch;
    }

    /** 按实际耗时把批大小调到"一帧装得下": 单条贵就少取几条, 便宜就多合并一些。 */
    private void Recalibrate(int processed, TimeSpan spent)
    {
        if (processed <= 0 || spent <= TimeSpan.Zero)
            return;
        _batch = Math.Clamp((int)(FrameBudgetMs * processed / spent.TotalMilliseconds), 1, MaxBatch);
    }
}