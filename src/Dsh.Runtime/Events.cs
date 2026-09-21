using Dsh.Runtime.Events;

namespace Dsh.Runtime;

public sealed record EventOptions
{
    public bool Prepend { get; init; }
    public bool Global { get; init; }
}

internal sealed class EventTable
{
    internal readonly Dictionary<Type, List<TypedHook>> TypedHooks = [];
    internal readonly Lock Sync = new();

    internal int RegisterTyped(TypedHook hook)
    {
        lock (Sync)
        {
            if (!TypedHooks.TryGetValue(hook.NotificationType, out var hooks))
                hooks = TypedHooks[hook.NotificationType] = [];
            if (hook.Options.Prepend)
                hooks.Insert(0, hook);
            else
                hooks.Add(hook);
            return hooks.Count;
        }
    }

    internal void UnregisterTyped(Type notificationType, INotificationDispatch handler)
    {
        lock (Sync)
        {
            if (!TypedHooks.TryGetValue(notificationType, out var hooks))
                return;
            var index = hooks.FindIndex(hook => ReferenceEquals(hook.Handler, handler));
            if (index < 0)
                return;
            hooks.RemoveAt(index);
            if (hooks.Count == 0)
                TypedHooks.Remove(notificationType);
        }
    }

    internal List<TypedHook> ResolveTyped(Type notificationType, Func<Context, bool>? filter)
    {
        List<TypedHook>? hooks;
        lock (Sync)
        {
            hooks = TypedHooks.TryGetValue(notificationType, out var found) ? [.. found] : null;
        }
        if (hooks is null)
            return [];
        return hooks
            .Where(hook => hook.Options.Global || filter is null || filter(hook.Ctx))
            .ToList();
    }
}

internal sealed record TypedHook(Context Ctx, INotificationDispatch Handler, EventOptions Options)
{
    public Type NotificationType => Handler.NotificationType;
}

/** 非泛型分发桥:存储与分发不依赖反射,AOT 友好。 */
internal interface INotificationDispatch
{
    Type NotificationType { get; }

    ValueTask<object?> Dispatch(object notification);

    ValueTask<object?> DispatchWaterfall(object notification, Func<ValueTask<object?>> next);
}

internal sealed class NotificationDispatch<TNotification>(object handler) : INotificationDispatch
    where TNotification : INotification
{
    public Type NotificationType => typeof(TNotification);

    public async ValueTask<object?> Dispatch(object notification)
    {
        var typed = (TNotification)notification;
        switch (handler)
        {
            case INotificationHandler<TNotification> plain:
                await plain.Handle(typed);
                return null;
            case IBailHandler<TNotification> bail:
                return await bail.Handle(typed);
            case IWaterfallHandler<TNotification> waterfall:
                return await waterfall.Handle(typed, static () => ValueTask.FromResult<object?>(null));
            default:
                throw new RuntimeException("INVALID_HANDLER", $"handler does not implement a notification interface for {typeof(TNotification).Name}");
        }
    }

    public async ValueTask<object?> DispatchWaterfall(object notification, Func<ValueTask<object?>> next)
    {
        var typed = (TNotification)notification;
        switch (handler)
        {
            case IWaterfallHandler<TNotification> waterfall:
                return await waterfall.Handle(typed, next);
            case INotificationHandler<TNotification> plain:
                await plain.Handle(typed);
                return await next();
            case IBailHandler<TNotification> bail:
                {
                    var result = await bail.Handle(typed);
                    return EventsService.IsBailed(result) ? result : await next();
                }
            default:
                throw new RuntimeException("INVALID_HANDLER", $"handler does not implement a notification interface for {typeof(TNotification).Name}");
        }
    }
}

/** 按 Type 注册的处理器:给无法引用 Dsh.Core 通知类型的宿主壳(如 Dsh.Boot)用,避免 MakeGenericMethod 在 AOT 下的实例化风险。 */
internal sealed class UntypedDispatch(Type notificationType, Func<object, ValueTask> handler) : INotificationDispatch
{
    public Type NotificationType { get; } = notificationType;

    public async ValueTask<object?> Dispatch(object notification)
    {
        await handler(notification);
        return null;
    }

    public async ValueTask<object?> DispatchWaterfall(object notification, Func<ValueTask<object?>> next)
    {
        await handler(notification);
        return await next();
    }
}

public sealed class EventsService
{
    private readonly Context _owner;
    private readonly EventTable _table;

    internal EventsService(Context owner, EventTable table)
    {
        _owner = owner;
        _table = table;
    }

    public static bool IsBailed(object? value) => value is not null and not false;

    public Func<bool> On<TNotification>(INotificationHandler<TNotification> handler, EventOptions? options = null)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterTyped<TNotification>(handler, options);
    }

    public Func<bool> OnBail<TNotification>(IBailHandler<TNotification> handler, EventOptions? options = null)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterTyped<TNotification>(handler, options);
    }

    public Func<bool> OnWaterfall<TNotification>(IWaterfallHandler<TNotification> handler, EventOptions? options = null)
        where TNotification : INotification
    {
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterTyped<TNotification>(handler, options);
    }

    public Func<bool> On(Type notificationType, Func<object, ValueTask> handler, EventOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(notificationType);
        ArgumentNullException.ThrowIfNull(handler);
        return RegisterTyped(new UntypedDispatch(notificationType, handler), notificationType, options);
    }

    public void Emit<TNotification>(TNotification notification) where TNotification : INotification
        => Emit(null, notification);

    public void Emit<TNotification>(Context? carrier, TNotification notification) where TNotification : INotification
    {
        foreach (var hook in ResolveTyped(carrier, notification))
            InvokeObserved(hook, notification);
    }

    public Task Parallel<TNotification>(TNotification notification) where TNotification : INotification
        => Parallel(null, notification);

    public async Task Parallel<TNotification>(Context? carrier, TNotification notification) where TNotification : INotification
    {
        var hooks = ResolveTyped(carrier, notification);
        var errors = await Task.WhenAll(hooks.Select(hook => InvokeSafe(hook, notification)));
        var failures = errors.Where(error => error is not null).Cast<Exception>().ToList();
        if (failures.Count > 0)
            throw new AggregateException(failures);
    }

    public ValueTask<object?> Serial<TNotification>(TNotification notification) where TNotification : INotification
        => Serial(null, notification);

    public async ValueTask<object?> Serial<TNotification>(Context? carrier, TNotification notification) where TNotification : INotification
    {
        foreach (var hook in ResolveTyped(carrier, notification))
        {
            var result = await hook.Handler.Dispatch(notification);
            if (IsBailed(result))
                return result;
        }
        return null;
    }

    public ValueTask<object?> Waterfall<TNotification>(TNotification notification, Func<ValueTask<object?>> inner)
        where TNotification : INotification
        => Waterfall(null, notification, inner);

    public async ValueTask<object?> Waterfall<TNotification>(Context? carrier, TNotification notification, Func<ValueTask<object?>> inner)
        where TNotification : INotification
    {
        var hooks = ResolveTyped(carrier, notification);
        var index = 0;
        async ValueTask<object?> Dispatch()
        {
            if (index >= hooks.Count)
                return await inner();
            var hook = hooks[index++];
            var called = false;
            ValueTask<object?> Next()
            {
                if (called)
                    throw new InvalidOperationException("next() called multiple times");
                called = true;
                return Dispatch();
            }
            return await hook.Handler.DispatchWaterfall(notification, Next);
        }
        return await Dispatch();
    }

    private Func<bool> RegisterTyped<TNotification>(object handler, EventOptions? options = null)
        where TNotification : INotification
        => RegisterTyped(new NotificationDispatch<TNotification>(handler), typeof(TNotification), options);

    private Func<bool> RegisterTyped(INotificationDispatch dispatch, Type notificationType, EventOptions? options)
    {
        _owner.Activation.AssertActive();
        var hook = new TypedHook(_owner, dispatch, options ?? new EventOptions());
        _table.RegisterTyped(hook);
        var effect = new EffectHandle($"on({notificationType.Name})", () =>
        {
            _table.UnregisterTyped(notificationType, dispatch);
            return Task.CompletedTask;
        });
        _owner.Activation.Effects.Add(effect);
        return () =>
        {
            effect.Dispose();
            return true;
        };
    }

    private List<TypedHook> ResolveTyped<TNotification>(Context? carrier, TNotification _)
        where TNotification : INotification
        => _table.ResolveTyped(typeof(TNotification), carrier?.Filter);

    private void InvokeObserved(TypedHook hook, object notification)
    {
        try
        {
            var task = hook.Handler.Dispatch(notification);
            if (!task.IsCompleted)
                _ = Observe(task);
            else
                task.GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            _owner.Root.Logger.Error("%s", error);
        }
    }

    private static async Task<Exception?> InvokeSafe(TypedHook hook, object notification)
    {
        try
        {
            await hook.Handler.Dispatch(notification);
            return null;
        }
        catch (Exception error)
        {
            return error;
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
            _owner.Root.Logger.Error("%s", error);
        }
    }
}
