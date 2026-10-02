using Dsh.Runtime.Plugins;

namespace Dsh.Runtime;

/** 单个插件的激活单元:公开可观测面(State/WaitAsync/Error 字符串),状态迁移由 PluginLifecycle 承载。 */
public sealed class PluginActivation
{
    private readonly PluginDefinition _definition;
    private readonly List<string> _provided = [];
    private readonly Lock _sync = new();
    private readonly Lock _inputSync = new();
    private readonly Lock _activationSync = new();
    private TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dictionary<string, PluginActivation> _dependencyOwners = new(StringComparer.Ordinal);

    public string Name { get; }
    public IReadOnlyList<string> Inject { get; }
    public object? Config { get; }
    public Context Ctx { get; private set; } = null!;

    /** 失败原因:只保留字符串,不缓存插件 Exception(卸载约束)。 */
    public string? Error { get; private set; }

    public ActivationState State { get; private set; } = ActivationState.Pending;
    public bool IsActive => State == ActivationState.Active;
    public bool IsTransitioning => State is ActivationState.Activating or ActivationState.Deactivating;

    internal PluginLifecycle? Lifecycle { get; }
    internal EffectScope Effects { get; } = new();
    internal bool PendingUnload { get; set; }

    /** 外部输入入口:并发提交在此串行化。 */
    public void SendInput<TInput>(in TInput input) where TInput : struct
    {
        if (Lifecycle is null)
            return;
        lock (_inputSync)
            Lifecycle.Input(in input);
    }

    internal PluginActivation(PluginDefinition definition, object? config)
    {
        _definition = definition;
        Name = definition.Name ?? "anonymous";
        Inject = definition.Inject;
        Config = config;
        Lifecycle = new PluginLifecycle(this);
    }

    private PluginActivation(string name)
    {
        _definition = PluginDefinition.From(static (_, _) => null, name);
        Name = name;
        Inject = [];
        State = ActivationState.Active;
        _settled.TrySetResult();
    }

    /** 宿主根激活:没有状态机,始终 Active。 */
    internal static PluginActivation CreateRoot() => new("root");

    public Task WaitAsync() => _settled.Task;

    /** 终态卸载:已注册到调度器的插件走协作式卸载,否则直接驱动状态机。 */
    public async Task DeactivateAsync()
    {
        if (State == ActivationState.Disposed)
            return;
        if (Ctx.Root.Scheduler.Find(Name) is not null)
        {
            await Ctx.Root.Scheduler.UnloadAsync(Name);
            return;
        }
        SendInput(new PluginLifecycleState.Input.Deactivate(Unload: true));
        await WaitAsync();
    }

    public void AssertActive()
    {
        if (State is ActivationState.Failed or ActivationState.Disposed)
            throw new RuntimeException(RuntimeException.InactiveEffect, "cannot create effect on inactive context");
    }

    internal void Attach(Context ctx) => Ctx = ctx;

    internal void TrackProvided(string name)
    {
        lock (_sync)
            _provided.Add(name);
    }

    public IReadOnlyList<string> ProvidedNames
    {
        get
        {
            lock (_sync)
                return [.. _provided];
        }
    }

    internal EffectHandle Effect(Func<object?> execute, string label)
    {
        AssertActive();
        var handle = EffectScope.Normalize(execute(), label);
        Effects.Add(handle);
        return handle;
    }

    /** 调度器同步驱动:Apply 成败以 ActivateCompleted 输入回灌状态机。 */
    internal bool TryStartExclusive(Func<PluginActivation, bool> canActivate)
    {
        lock (_activationSync)
        {
            if (State != ActivationState.Pending || !canActivate(this))
                return false;
            StartActivation();
            return true;
        }
    }

    internal bool TryRequestRebuildExclusive(Func<PluginActivation, bool> canActivate)
    {
        lock (_activationSync)
        {
            if (State != ActivationState.Active || canActivate(this))
                return false;
            RequestRebuild();
            return true;
        }
    }

    internal void StartActivation()
    {
        SendInput(new PluginLifecycleState.Input.Activate());
        SendInput(new PluginLifecycleState.Input.ActivateCompleted(Apply().Error));
    }

    internal void RequestRebuild()
    {
        PendingUnload = false;
        SendInput(new PluginLifecycleState.Input.DependencyChanged());
    }

    internal void RequestUnload() => SendInput(new PluginLifecycleState.Input.Deactivate(Unload: true));

    internal void ClearError() => Error = null;

    /** 依赖可用性:注入项存在且当前可注入(供激活资格判定)。 */
    internal bool DependenciesAvailable(IReadOnlyDictionary<string, PluginActivation> providers)
    {
        foreach (var name in Inject)
        {
            if (!providers.ContainsKey(name) || !Ctx.Root.IsServiceInjectable(name))
                return false;
        }
        return true;
    }

    /** 依赖提供者是否已变更(供 Active 插件的重建 (i) 判定)。 */
    internal bool DependencyProvidersChanged(IReadOnlyDictionary<string, PluginActivation> providers)
    {
        foreach (var (name, owner) in _dependencyOwners)
        {
            if (!providers.TryGetValue(name, out var current) || !ReferenceEquals(current, owner))
                return true;
        }
        return false;
    }

    internal bool IsStale(IReadOnlyDictionary<string, PluginActivation> providers)
        => !DependenciesAvailable(providers) || DependencyProvidersChanged(providers);

    internal void CaptureDependencies(IReadOnlyDictionary<string, PluginActivation> providers)
    {
        var snapshot = new Dictionary<string, PluginActivation>(StringComparer.Ordinal);
        foreach (var name in Inject)
        {
            if (providers.TryGetValue(name, out var owner))
                snapshot[name] = owner;
        }
        _dependencyOwners = snapshot;
    }

    internal void LifecycleEntered(ActivationState state)
    {
        State = state;
        switch (state)
        {
            case ActivationState.Activating:
            case ActivationState.Deactivating:
                _settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                break;
            case ActivationState.Active:
                Ctx.LoggerFor().Info("activated");
                _settled.TrySetResult();
                Ctx.Root.Scheduler.OnActivationStateChanged(this, state);
                break;
            case ActivationState.Disposed:
                Ctx.LoggerFor().Info("deactivated");
                _settled.TrySetResult();
                Ctx.Root.Scheduler.OnActivationStateChanged(this, state);
                break;
            default:
                _settled.TrySetResult();
                break;
        }
    }

    internal async Task<bool> DisposeEffectsAsync()
    {
        await Effects.DisposeAllAsync(error => Ctx.Logger.Error("%s", error));
        return PendingUnload;
    }

    private ApplyOutcome Apply()
    {
        PluginApplyScope.Enter(Name);
        try
        {
            var result = _definition.Apply(Ctx, Config);
            if (result is not null)
                Effects.Add(EffectScope.Normalize(result, $"apply({Name})"));
            return default;
        }
        catch (Exception error)
        {
            Error = DeepestMessage(error);
            Ctx.Logger.Error("%s", error);
            return new ApplyOutcome(Error);
        }
        finally
        {
            PluginApplyScope.Exit();
        }
    }

    private static string DeepestMessage(Exception error)
    {
        while (error.InnerException is not null)
            error = error.InnerException;
        return error.Message;
    }

    private readonly record struct ApplyOutcome(string? Error);
}
