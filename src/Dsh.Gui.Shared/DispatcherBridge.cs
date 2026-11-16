using Avalonia.Threading;

namespace Dsh.Gui.Services;

/** 后台线程产生的事件先入队, 合并后一次性投递到 UI 线程, 避免每个 token 一次 Post。 */
public sealed class DispatcherBridge<T>(Action<IReadOnlyList<T>> drain)
{
    private readonly object _gate = new();
    private readonly List<T> _pending = [];
    private bool _scheduled;

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
        T[] batch;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
            _scheduled = false;
        }
        if (batch.Length == 0)
            return;
        drain(batch);
    }
}
