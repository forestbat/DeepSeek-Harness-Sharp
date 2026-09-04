using System.Runtime.CompilerServices;

namespace Cordis;

public enum FiberState
{
    Pending,
    Loading,
    Active,
    Failed,
    Disposed,
    Unloading,
}

public sealed class ValidationIssue
{
    public required string Message { get; init; }
    public IReadOnlyList<string>? Path { get; init; }
}

public sealed class ValidationError : Exception
{
    public IReadOnlyList<ValidationIssue> Issues { get; }

    public ValidationError(IReadOnlyList<ValidationIssue> issues)
        : base("invalid config:\n" + string.Join('\n', issues.Select(issue =>
            issue.Path is { Count: > 0 } path
                ? $"  - {issue.Message} (at {string.Join('.', path)})"
                : $"  - {issue.Message}")))
    {
        Issues = issues;
    }
}

public interface IConfigValidator
{
    object? Validate(object? config);
}

public sealed record EffectMeta(string Label)
{
    public List<EffectMeta> Children { get; } = [];
}

public sealed class EffectHandle
{
    private readonly Func<Task> _dispose;
    private bool _active = true;

    public EffectMeta Meta { get; }
    public bool IsActive => _active;
    public Task? EffectTask { get; internal set; }

    internal EffectHandle(EffectMeta meta, Func<Task> dispose)
    {
        Meta = meta;
        _dispose = dispose;
    }

    public async Task DisposeAsync()
    {
        if (!_active) return;
        _active = false;
        if (EffectTask is not null) await EffectTask;
        await _dispose();
    }

    public void Dispose() => _ = Observe(DisposeAsync());

    private async Task Observe(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // 与 JS 一致：dispose 失败不应成为未处理异常
        }
    }
}

public sealed class Fiber
{
    internal const string InactiveEpoch = "__INACTIVE__";

    public int? Uid { get; private set; }
    public Context Ctx { get; }
    public object? Config { get; internal set; }
    public FiberState State { get; private set; } = FiberState.Pending;
    public Dictionary<string, object?> Inject { get; }
    public PluginRuntime? Runtime { get; }
    public Context Parent { get; }
    public Dictionary<string, Impl>? Store { get; internal set; }
    public Task? Inertia { get; private set; }
    public Func<Task> Dispose { get; }
    public Loader.Entry? Entry { get; internal set; }

    internal readonly Dictionary<string, DisposableList<EventListener>> Hooks = new();
    internal readonly DisposableList<EffectHandle> Disposables = new();

    private object? _error;
    private string _epoch = InactiveEpoch;
    private readonly Dictionary<string, Impl> _pendingStore = new();

    public Fiber(Context parent, object? config, Dictionary<string, object?> inject, PluginRuntime? runtime)
    {
        Parent = parent;
        Config = config;
        Inject = inject;
        Runtime = runtime;
        if (runtime is not null)
        {
            Uid = parent.Registry.Counter;
            Ctx = parent.Extend((Context.FiberKey, this));
            if (inject.Count > 0)
            {
                var intercept = parent.InterceptMap.Derive();
                foreach (var (name, value) in inject)
                {
                    if (value is not null) intercept[name] = value;
                }
                Ctx.SetOwn(Symbols.Intercept, intercept);
            }
            Ctx.Events.Emit(null, EventNames.Plugin, this);
            foreach (var name in inject.Keys)
            {
                CheckImpl(name);
            }
            Dispose = parent.Fiber.Effect(() =>
            {
                var remove = runtime.Fibers.Push(this);
                try
                {
                    Config = ResolveConfig(runtime, config);
                    Refresh();
                }
                catch (Exception error)
                {
                    Ctx.Logger.Error("%s", error);
                    _error = error;
                }
                return (Func<Task>)(async () =>
                {
                    Uid = null;
                    Ctx.Events.Emit(null, EventNames.Plugin, this);
                    if (Ctx.Registry.Has(runtime.Callback))
                    {
                        remove();
                        if (runtime.Fibers.Length == 0)
                        {
                            Ctx.Registry.Delete(runtime.Callback);
                        }
                    }
                    SetEpoch(InactiveEpoch);
                    while (Inertia is not null)
                    {
                        await Inertia;
                    }
                });
            }, "ctx.plugin()").DisposeAsync;
        }
        else
        {
            Uid = 0;
            Ctx = parent;
            State = FiberState.Active;
            Store = [];
            Dispose = () => Restart();
        }
    }

    public string Name
    {
        get
        {
            var fiber = this;
            while (true)
            {
                if (fiber.Runtime?.Name is { } name) return name;
                var next = fiber.Parent.Fiber;
                if (ReferenceEquals(next, fiber)) return "root";
                fiber = next;
            }
        }
    }

    public void AssertActive()
    {
        if (Uid is not null) return;
        throw new CordisException(CordisException.InactiveEffect, "cannot create effect on inactive context");
    }

    public static object? ResolveConfig(PluginRuntime runtime, object? config)
    {
        if (runtime.ConfigValidator is null) return config;
        return runtime.ConfigValidator.Validate(config);
    }

    public EffectHandle Effect(Func<object?> execute, string label = "anonymous")
    {
        AssertActive();
        var disposables = new List<EffectHandle>();
        var meta = new EffectMeta(label);
        var runnerActive = true;

        async Task DisposeAll()
        {
            Task? task = null;
            var items = disposables.ToList();
            disposables.Clear();
            items.Reverse();
            foreach (var dispose in items)
            {
                if (task is not null)
                {
                    var previous = task;
                    task = previous.ContinueWith(_ => dispose.DisposeAsync(), TaskScheduler.Default).Unwrap();
                }
                else
                {
                    task = dispose.DisposeAsync();
                }
            }
            if (task is not null) await task;
        }

        void Collect(EffectHandle handle)
        {
            disposables.Add(handle);
            Disposables.Delete(handle);
            meta.Children.Add(handle.Meta);
        }

        Task? task;
        try
        {
            task = ExecuteEffect(execute, Collect, () => runnerActive);
        }
        catch
        {
            _ = DisposeAll();
            throw;
        }

        if (task is not null)
        {
            _ = ObserveFailure(task);
        }

        var handle = new EffectHandle(meta, DisposeAll);
        handle.EffectTask = task;
        Disposables.Push(handle);
        return handle;

        async Task ObserveFailure(Task t)
        {
            try
            {
                await t;
            }
            catch (Exception error)
            {
                await DisposeAll();
                Ctx.Logger.Error("%s", error);
            }
        }
    }

    internal EffectHandle EffectSync(Func<Action> execute, string label)
    {
        return Effect(() =>
        {
            var dispose = execute();
            return (Func<Task>)(() =>
            {
                dispose();
                return Task.CompletedTask;
            });
        }, label);
    }

    private Task? ExecuteEffect(Func<object?> execute, Action<EffectHandle> collect, Func<bool> isCurrent)
    {
        var result = execute();
        return NormalizeEffect(result, collect, isCurrent);
    }

    private Task? NormalizeEffect(object? result, Action<EffectHandle> collect, Func<bool> isCurrent)
    {
        switch (result)
        {
            case null:
                return null;
            case EffectHandle handle:
                collect(handle);
                return null;
            case Action action:
                collect(Wrap(() =>
                {
                    action();
                    return Task.CompletedTask;
                }));
                return null;
            case Func<Task> asyncDispose:
                collect(Wrap(asyncDispose));
                return null;
            case Delegate dispose:
                collect(Wrap(() =>
                {
                    if (dispose.DynamicInvoke() is Task inner) return inner;
                    return Task.CompletedTask;
                }));
                return null;
            case Task<object?> task:
                return AwaitAndNormalize(task, collect, isCurrent);
            case IAsyncEnumerable<object?> asyncIterable:
                return DrainAsync(asyncIterable, collect, isCurrent);
            case System.Collections.IEnumerable iterable:
                foreach (var item in iterable)
                {
                    collect(ToHandle(item));
                }
                return null;
            default:
                throw new CordisException("INVALID_EFFECT", "Invalid effect");
        }
    }

    private async Task AwaitAndNormalize(Task<object?> task, Action<EffectHandle> collect, Func<bool> isCurrent)
    {
        var result = await task;
        if (!isCurrent()) return;
        var inner = NormalizeEffect(result, collect, isCurrent);
        if (inner is not null) await inner;
    }

    private async Task DrainAsync(IAsyncEnumerable<object?> iterable, Action<EffectHandle> collect, Func<bool> isCurrent)
    {
        await Task.Yield();
        await foreach (var item in iterable)
        {
            if (!isCurrent()) return;
            collect(ToHandle(item));
        }
    }

    private EffectHandle ToHandle(object? item)
    {
        return item switch
        {
            null => Wrap(() => Task.CompletedTask),
            EffectHandle h => h,
            Action a => Wrap(() =>
            {
                a();
                return Task.CompletedTask;
            }),
            Func<Task> f => Wrap(f),
            Delegate d => Wrap(() =>
            {
                if (d.DynamicInvoke() is Task inner) return inner;
                return Task.CompletedTask;
            }),
            _ => throw new CordisException("INVALID_EFFECT", "Invalid effect"),
        };
    }

    private EffectHandle Wrap(Func<Task> dispose) => new(new EffectMeta("anonymous"), dispose);

    public IReadOnlyList<EffectMeta> GetEffects()
    {
        return Disposables.Select(d => d.Meta).ToList();
    }

    private FiberState GetState()
    {
        if (Uid is null) return FiberState.Disposed;
        if (_error is not null) return FiberState.Failed;
        if (_epoch != InactiveEpoch) return FiberState.Active;
        return FiberState.Pending;
    }

    private void UpdateState(Func<FiberState?> callback)
    {
        var oldState = State;
        State = callback() ?? GetState();
        if (oldState == State) return;
        Ctx.Events.Emit(null, EventNames.Status, this, oldState);
        if (oldState != FiberState.Active && State != FiberState.Active) return;
        foreach (var (_, impl) in Ctx.Reflect.Store.ToList())
        {
            if (!ReferenceEquals(impl.Fiber, this)) continue;
            Ctx.Reflect.Notify([impl.Name]);
        }
    }

    internal void CheckImpl(string name)
    {
        var impl = Ctx.Reflect.GetImpl(name, true);
        if (impl is null)
        {
            _pendingStore.Remove(name);
            return;
        }
        try
        {
            if (impl.Check is not null && !impl.Check())
            {
                _pendingStore.Remove(name);
                return;
            }
        }
        catch (Exception error)
        {
            impl.Fiber.Ctx.Logger.Error("%s", error);
            _pendingStore.Remove(name);
            return;
        }
        _pendingStore[name] = impl;
    }

    internal void Refresh()
    {
        var epoch = "";
        foreach (var name in Inject.Keys)
        {
            if (!_pendingStore.TryGetValue(name, out var impl))
            {
                epoch = InactiveEpoch;
                SetEpoch(epoch);
                return;
            }
            epoch += $":{impl.Fiber.Uid}";
        }
        SetEpoch(epoch);
    }

    private void SetEpoch(string epoch)
    {
        var oldEpoch = _epoch;
        if (epoch == oldEpoch) return;
        if (_error is not null) return;
        _epoch = epoch;
        if (Inertia is not null) return;
        UpdateState(() =>
        {
            if (epoch != InactiveEpoch && oldEpoch == InactiveEpoch)
            {
                Inertia = Reload();
                return FiberState.Loading;
            }
            Inertia = Unload();
            return FiberState.Unloading;
        });
    }

    private async Task Reload()
    {
        Store = new Dictionary<string, Impl>(_pendingStore);
        var oldEpoch = _epoch;
        try
        {
            await Task.Yield();
            var task = ExecuteEffect(ExecuteMain, d => Disposables.Push(d), () => _epoch == oldEpoch);
            if (task is not null) await task;
        }
        catch (Exception error)
        {
            Ctx.Logger.Error("%s", error);
            _error = error;
            _epoch = InactiveEpoch;
        }
        UpdateState(() =>
        {
            if (_epoch == oldEpoch)
            {
                Inertia = null;
                return null;
            }
            Inertia = Unload();
            return FiberState.Unloading;
        });
    }

    private object? ExecuteMain()
    {
        return Runtime!.Callback.Invoke(Ctx, Config);
    }

    private async Task Unload()
    {
        await Task.Yield();
        var disposables = Disposables.Clear();
        await Task.WhenAll(disposables.Select(async dispose =>
        {
            try
            {
                await Task.Yield();
                await dispose.DisposeAsync();
            }
            catch (Exception error)
            {
                Ctx.Logger.Error("%s", error);
            }
        }));
        Store = null;
        UpdateState(() =>
        {
            if (_epoch == InactiveEpoch)
            {
                Inertia = null;
                return null;
            }
            Inertia = Reload();
            return FiberState.Loading;
        });
    }

    public async Task<Fiber> Await()
    {
        while (Inertia is not null)
        {
            await Inertia;
        }
        if (_error is not null) throw new CordisException("PLUGIN_FAILED", _error.ToString());
        return this;
    }

    public async Task Restart()
    {
        var fiber = Ctx.Fiber;
        fiber.AssertActive();
        fiber.SetEpoch(InactiveEpoch);
        fiber.Refresh();
        await fiber.Await();
    }

    public void Update(object? config, bool noSave = false)
    {
        var fiber = Ctx.Fiber;
        fiber.AssertActive();
        config = ResolveConfig(fiber.Runtime!, config);
        fiber.Ctx.Events.WaterfallSync(fiber, EventNames.Update, [config, noSave], () =>
        {
            fiber.Config = config;
            fiber._error = null;
            _ = fiber.Restart();
            return null;
        });
    }
}
