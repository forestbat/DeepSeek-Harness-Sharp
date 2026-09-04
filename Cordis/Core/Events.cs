namespace Cordis;

public delegate ValueTask<object?> EventListener(object? thisArg, object?[] args);

public enum DispatchMode
{
    Emit,
    Parallel,
    Serial,
    Bail,
    Waterfall,
}

public sealed record EventOptions
{
    public bool Prepend { get; init; }
    public bool Global { get; init; }
}

public sealed record Hook
{
    public required Context Ctx { get; init; }
    public required EventListener Callback { get; init; }
    public required EventOptions Options { get; init; }
}

public static class EventNames
{
    public const string Plugin = "internal/plugin";
    public const string Status = "internal/status";
    public const string Service = "internal/service";
    public const string Update = "internal/update";
    public const string Get = "internal/get";
    public const string Set = "internal/set";
    public const string Listener = "internal/listener";
    public const string Dispatch = "internal/dispatch";
}

public sealed class EventsService
{
    private static readonly HashSet<string> ReservedWords = ["prototype", "then"];

    internal readonly Dictionary<object, List<Hook>> Hooks;
    private readonly Context _ctx;

    public EventsService(Context ctx) : this(ctx, null)
    {
        On(EventNames.Listener, (thisArg, args) =>
        {
            var name = (string)args[0]!;
            var listener = (EventListener)args[1]!;
            var options = (EventOptions)args[2]!;
            var self = (Context)thisArg!;
            if (name == EventNames.Update && !options.Global)
            {
                if (!self.Fiber.Hooks.TryGetValue(EventNames.Update, out var hooks))
                {
                    hooks = self.Fiber.Hooks[EventNames.Update] = new DisposableList<EventListener>();
                }
                var remove = options.Prepend ? hooks.Unshift(listener) : hooks.Push(listener);
                return new ValueTask<object?>(new DisposeFunc(() =>
                {
                    remove();
                    return true;
                }));
            }
            return new ValueTask<object?>((object?)null);
        });

        On(EventNames.Update, (thisArg, args) =>
        {
            var self = (Fiber)thisArg!;
            var config = args[0];
            var noSave = args[1];
            var next = (Func<object?>)args[2]!;
            var cbs = self.Hooks.TryGetValue(EventNames.Update, out var list)
                ? list.ToList()
                : new List<EventListener>();
            object? Next()
            {
                if (cbs.Count == 0) return next();
                var cb = cbs[0];
                cbs.RemoveAt(0);
                var task = cb(self, [config, noSave, (Func<object?>)Next]);
                if (!task.IsCompleted) throw new CordisException("ASYNC_IN_SYNC", "async listener is not supported in sync waterfall");
                return task.GetAwaiter().GetResult();
            }
            return new ValueTask<object?>(Next());
        }, new EventOptions { Global = true, Prepend = true });
    }

    private EventsService(Context ctx, EventsService? prototype)
    {
        _ctx = ctx;
        Hooks = prototype?.Hooks ?? new Dictionary<object, List<Hook>>();
    }

    public EventsService Bind(Context ctx) => new(ctx, this);

    public static bool IsBailed(object? value) => value is not null && value is not false;

    private (object? ThisArg, List<EventListener> Callbacks) Resolve(DispatchMode type, object? thisArg, object name, object?[] args)
    {
        if (name is not string s || !s.StartsWith("internal/"))
        {
            if (Hooks.TryGetValue(EventNames.Dispatch, out var dispatch) && dispatch.Count > 0)
            {
                Emit(null, EventNames.Dispatch, type, name, args, thisArg);
            }
        }
        Func<Context, bool>? filter = (thisArg as Context)?.Filter;
        var callbacks = Hooks.TryGetValue(name, out var hooks)
            ? hooks.Where(hook => hook.Options.Global || filter is null || filter(hook.Ctx)).Select(h => h.Callback).ToList()
            : [];
        return (thisArg, callbacks);
    }

    public void Emit(object? thisArg, object name, params object?[] args)
    {
        var (thisArg0, callbacks) = Resolve(DispatchMode.Emit, thisArg, name, args);
        foreach (var callback in callbacks)
        {
            InvokeObserved(callback, thisArg0, args);
        }
    }

    public async Task Parallel(object? thisArg, object name, params object?[] args)
    {
        var (thisArg0, callbacks) = Resolve(DispatchMode.Parallel, thisArg, name, args);
        var results = await Task.WhenAll(callbacks.Select(InvokeSafely));
        var errors = results.Where(e => e is not null).Cast<Exception>().ToList();
        if (errors.Count > 0) throw new AggregateException(errors);

        async Task<Exception?> InvokeSafely(EventListener callback)
        {
            try
            {
                await callback(thisArg0, args);
                return null;
            }
            catch (Exception error)
            {
                return error;
            }
        }
    }

    public async ValueTask<object?> Serial(object? thisArg, object name, params object?[] args)
    {
        var (thisArg0, callbacks) = Resolve(DispatchMode.Serial, thisArg, name, args);
        foreach (var callback in callbacks)
        {
            var result = await callback(thisArg0, args);
            if (IsBailed(result)) return result;
        }
        return null;
    }

    public async ValueTask<object?> Bail(object? thisArg, object name, params object?[] args)
    {
        var (thisArg0, callbacks) = Resolve(DispatchMode.Bail, thisArg, name, args);
        foreach (var callback in callbacks)
        {
            var result = await callback(thisArg0, args);
            if (IsBailed(result)) return result;
        }
        return null;
    }

    public async ValueTask<object?> Waterfall(object? thisArg, object name, object?[] args, Func<ValueTask<object?>> inner)
    {
        var (thisArg0, callbacks) = Resolve(DispatchMode.Waterfall, thisArg, name, args);
        var index = 0;
        async ValueTask<object?> Dispatch()
        {
            if (index >= callbacks.Count) return await inner();
            var callback = callbacks[index++];
            var called = false;
            ValueTask<object?> Next()
            {
                if (called) throw new InvalidOperationException("next() called multiple times");
                called = true;
                return Dispatch();
            }
            return await callback(thisArg0, [.. args, (Func<ValueTask<object?>>)Next]);
        }
        return await Dispatch();
    }

    public Func<bool> On(object name, EventListener listener, EventOptions? options = null)
    {
        options ??= new EventOptions();
        _ctx.Fiber.AssertActive();
        var result = BailSync(_ctx, EventNames.Listener, name, listener, options);
        if (result is Func<bool> dispose0) return dispose0;
        if (result is DisposeFunc df) return df.Invoke;
        var label = name is string s ? $"ctx.on({s})" : $"ctx.on({name})";
        return Register(label, name, listener, options);
    }

    public Func<bool> Once(object name, EventListener listener, EventOptions? options = null)
    {
        Func<bool>? dispose = null;
        dispose = On(name, async (thisArg, args) =>
        {
            dispose!();
            return await listener(thisArg, args);
        }, options);
        return dispose;
    }

    private Func<bool> Register(string label, object name, EventListener listener, EventOptions options)
    {
        var handle = _ctx.Fiber.EffectSync(() =>
        {
            if (!Hooks.TryGetValue(name, out var hooks)) hooks = Hooks[name] = [];
            if (options.Prepend) hooks.Insert(0, new Hook { Ctx = _ctx, Callback = listener, Options = options });
            else hooks.Add(new Hook { Ctx = _ctx, Callback = listener, Options = options });
            return () => Unregister(name, listener);
        }, label);
        return () =>
        {
            handle.Dispose();
            return true;
        };
    }

    private bool Unregister(object name, EventListener callback)
    {
        if (!Hooks.TryGetValue(name, out var hooks)) return false;
        var index = hooks.FindIndex(hook => ReferenceEquals(hook.Callback, callback));
        if (index < 0) return false;
        hooks.RemoveAt(index);
        if (hooks.Count == 0) Hooks.Remove(name);
        return true;
    }

    internal object? BailSync(object? thisArg, object name, params object?[] args)
    {
        var (thisArg0, callbacks) = Resolve(DispatchMode.Bail, thisArg, name, args);
        foreach (var callback in callbacks)
        {
            var task = callback(thisArg0, args);
            if (!task.IsCompleted) throw new CordisException("async listener is not supported in sync dispatch");
            var result = task.GetAwaiter().GetResult();
            if (IsBailed(result)) return result;
        }
        return null;
    }

    internal object? WaterfallSync(object? thisArg, object name, object?[] args, Func<object?> inner)
    {
        var (thisArg0, callbacks) = Resolve(DispatchMode.Waterfall, thisArg, name, args);
        var index = 0;
        object? Dispatch()
        {
            if (index >= callbacks.Count) return inner();
            var callback = callbacks[index++];
            var called = false;
            object? Next()
            {
                if (called) throw new InvalidOperationException("next() called multiple times");
                called = true;
                return Dispatch();
            }
            var task = callback(thisArg0, [.. args, (Func<object?>)Next]);
            if (!task.IsCompleted) throw new CordisException("async listener is not supported in sync waterfall");
            return task.GetAwaiter().GetResult();
        }
        return Dispatch();
    }

    private void InvokeObserved(EventListener callback, object? thisArg, object?[] args)
    {
        try
        {
            var task = callback(thisArg, args);
            if (!task.IsCompleted) _ = Observe(task);
            else task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            _ctx.Root.Logger.Error("%s", error);
        }
    }

    private async Task Observe(ValueTask<object?> task)
    {
        try
        {
            await task;
        }
        catch (Exception error)
        {
            _ctx.Root.Logger.Error("%s", error);
        }
    }

    internal sealed record DisposeFunc(Func<bool> Invoke);
}

public class CordisException : Exception
{
    public string Code { get; }

    public CordisException(string code, string? message = null)
        : base(message ?? code)
    {
        Code = code;
    }

    public const string InactiveEffect = "INACTIVE_EFFECT";
}
