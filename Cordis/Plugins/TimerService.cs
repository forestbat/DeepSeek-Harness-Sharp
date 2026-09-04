using System.Runtime.CompilerServices;

namespace Cordis.Plugins;

public class TimerService : Service
{
    public TimerService(Context ctx) : base(ctx, "timer")
    {
        ctx.Mixin("timer", "Timeout", "Interval", "Throttle", "Debounce", "SetTimeout", "SetInterval");
    }

    public EffectHandle SetTimeout(Action callback, long delay) => Timeout(callback, delay);

    public EffectHandle SetInterval(Action callback, long delay) => Interval(callback, delay);

    public EffectHandle Timeout(Action callback, long delay)
    {
        var cts = new CancellationTokenSource();
        EffectHandle? self = null;
        self = Ctx.Effect(() =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                if (self is not null) await self.DisposeAsync();
                callback();
            });
            return () => cts.Cancel();
        }, "ctx.timeout()");
        return self;
    }

    public Task Timeout(long delay)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource();
        var handle = Ctx.Effect(() => (Func<Task>)(() =>
        {
            cts.Cancel();
            completion.TrySetCanceled();
            return Task.CompletedTask;
        }), "ctx.timeout()");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cts.Token);
                completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
            }
        });
        return completion.Task.ContinueWith(_ => handle.Dispose(), TaskScheduler.Default);
    }

    public EffectHandle Interval(Action callback, long delay)
    {
        var cts = new CancellationTokenSource();
        return Ctx.Effect(() =>
        {
            _ = Task.Run(async () =>
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(delay));
                try
                {
                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        callback();
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
            return () =>
            {
                cts.Cancel();
            };
        }, "ctx.interval()");
    }

    public async IAsyncEnumerable<object?> Interval(long delay, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(delay));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            yield return null;
        }
    }

    private Func<object?[], object?> Schedule(string label, Func<object?[], bool, IDisposable?> trigger)
    {
        var disposed = false;
        IDisposable? timer = null;
        var handle = Ctx.Effect(() => () =>
        {
            disposed = true;
            timer?.Dispose();
        }, label);
        return args =>
        {
            timer?.Dispose();
            timer = trigger(args, disposed);
            return handle;
        };
    }

    public Func<object?[], object?> Throttle(Action<object?[]> callback, long delay, bool noTrailing = false)
    {
        var lastCall = DateTimeOffset.MinValue;
        return Schedule("ctx.throttle()", (args, disposed) =>
        {
            var now = DateTimeOffset.UtcNow;
            var remaining = delay - (long)(now - lastCall).TotalMilliseconds;
            if (remaining <= 0)
            {
                lastCall = now;
                callback(args);
                return null;
            }
            if (disposed || noTrailing) return null;
            return SetTimer(remaining, () =>
            {
                lastCall = DateTimeOffset.UtcNow;
                callback(args);
            });
        });
    }

    public Func<object?[], object?> Debounce(Action<object?[]> callback, long delay)
    {
        return Schedule("ctx.debounce()", (args, disposed) =>
        {
            if (disposed) return null;
            return SetTimer(delay, () => callback(args));
        });
    }

    private static IDisposable SetTimer(long delay, Action callback)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), cts.Token);
                callback();
            }
            catch (OperationCanceledException)
            {
            }
        });
        return cts;
    }
}
