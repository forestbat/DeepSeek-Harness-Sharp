namespace Dsh.Runtime;

/** 单个插件的激活单元:公开可观测面(State/WaitAsync/Error 字符串),状态迁移由本类内的转移表承载。 */
public sealed class PluginActivation
{
    private readonly PluginDefinition _definition;
    private readonly List<string> _provided = [];
    private readonly Lock _sync = new();
    private readonly Lock _inputSync = new();
    private readonly Lock _activationSync = new();
    private readonly Lock _stateSync = new();
    private CancellationTokenSource _activationCancellation = new();
    private TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Dictionary<string, PluginActivation> _dependencyOwners = new(StringComparer.Ordinal);

    private bool ActivationCanceled
    {
        get
        {
            lock (_stateSync)
                return _activationCancellation.IsCancellationRequested;
        }
    }

    private CancellationToken ActivationToken
    {
        get
        {
            lock (_stateSync)
                return _activationCancellation.Token;
        }
    }

    public string Name { get; }
    public IReadOnlyList<string> Inject { get; }

    /** 类型化依赖边:激活时按可赋值性解析成服务名并捕获,此后与名字边走同一套失效/重建机制。 */
    public IReadOnlyList<Type> InjectTypes { get; }
    public object? Config { get; }
    public Context Ctx { get; private set; } = null!;

    /** 失败原因:只保留字符串,不缓存插件 Exception(卸载约束)。 */
    public string? Error { get; private set; }

    /** Read under the same lock that completes the settle handle: a caller that sees a settled state also sees WaitAsync completed. */
    public ActivationState State
    {
        get
        {
            lock (_stateSync)
                return field;
        }
        private set;
    } = ActivationState.Pending;
    public bool IsActive => State == ActivationState.Active;
    public bool IsTransitioning => State is ActivationState.Activating or ActivationState.Deactivating;

    internal EffectScope Effects { get; } = new();
    internal bool PendingUnload { get; set; }

    /** 外部输入入口:并发提交在此串行化。 */
    public void SendInput(in PluginActivationInput input)
    {
        lock (_inputSync)
            Transition(input);
    }

    internal PluginActivation(PluginDefinition definition, object? config)
    {
        _definition = definition;
        Name = definition.Name ?? "anonymous";
        Inject = definition.Inject;
        InjectTypes = definition.InjectTypes;
        Config = config;
    }

    private PluginActivation(string name)
    {
        _definition = PluginDefinition.From(static (_, _, _) => Task.FromResult<object?>(null), name);
        Name = name;
        Inject = [];
        InjectTypes = [];
        State = ActivationState.Active;
        _settled.TrySetResult();
    }

    /** 宿主根激活:没有状态机,始终 Active。 */
    internal static PluginActivation CreateRoot() => new("root");

    public Task WaitAsync()
    {
        lock (_stateSync)
            return _settled.Task;
    }

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
        SendInput(new PluginActivationInput(PluginActivationInputKind.Deactivate, Unload: true));
        await WaitAsync();
    }

    public void AssertActive()
    {
        // 效应/服务只允许在 Activating/Active 期间登记; 取消后的旧 activation 不得再发布服务。
        if (State is not (ActivationState.Activating or ActivationState.Active) || ActivationCanceled)
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
        lock (_stateSync)
        {
            _activationCancellation.Dispose();
            _activationCancellation = new CancellationTokenSource();
        }
        SendInput(new PluginActivationInput(PluginActivationInputKind.Activate));
        _ = ActivateAsync();
    }

    internal void RequestRebuild()
    {
        // PendingUnload 的复位放到状态转移内(HandleStale), 避免在输入串行化之外与卸载请求互相覆盖。
        SendInput(new PluginActivationInput(PluginActivationInputKind.DependencyChanged));
    }

    internal void RequestUnload() => SendInput(new PluginActivationInput(PluginActivationInputKind.Deactivate, Unload: true));

    internal void ClearError() => Error = null;

    /** 调度器反查用:声明的名字边 + 激活时捕获的类型边(已落成服务名)。 */
    internal bool DependsOn(string serviceName)
        => Inject.Contains(serviceName, StringComparer.Ordinal)
            || Volatile.Read(ref _dependencyOwners).ContainsKey(serviceName);

    /** 依赖可用性:注入项存在且当前可注入(供激活资格判定)。 */
    internal bool DependenciesAvailable(IReadOnlyDictionary<string, PluginActivation> providers)
    {
        foreach (var name in Inject)
        {
            if (!providers.ContainsKey(name) || !Ctx.Root.IsServiceInjectable(name))
                return false;
        }
        foreach (var type in InjectTypes)
        {
            if (!Ctx.Root.IsServiceInjectable(type))
                return false;
        }
        return true;
    }

    /** 依赖提供者是否已变更(供 Active 插件的重建 (i) 判定)。 */
    internal bool DependencyProvidersChanged(IReadOnlyDictionary<string, PluginActivation> providers)
    {
        foreach (var (name, owner) in Volatile.Read(ref _dependencyOwners))
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
        foreach (var type in InjectTypes)
        {
            var resolved = Ctx.Root.ServiceTable.ResolveName(type);
            if (resolved is not null && providers.TryGetValue(resolved, out var owner))
                snapshot[resolved] = owner;
        }
        // 捕获发生在激活线程, 失效判定发生在调度泵线程: 引用必须以 release/acquire 语义发布, 否则泵可能读到上一个提供者。
        Volatile.Write(ref _dependencyOwners, snapshot);
    }

    /** 状态转移表(替代 LogicBlocks, D7): 只决定"下一个状态", 副作用统一走 LifecycleEntered/StartDisposal。 */
    private void Transition(in PluginActivationInput input)
    {
        switch (input.Kind)
        {
            case PluginActivationInputKind.Activate when State == ActivationState.Pending:
                LifecycleEntered(ActivationState.Activating);
                break;
            case PluginActivationInputKind.ActivateCompleted when State == ActivationState.Activating:
                if (PendingUnload)
                {
                    // 激活期间收到卸载: 先撤掉本次 Apply 登记的效应, 再走正常处置路径落到 Disposed;
                    // 不能先发布 Active, 否则 UnloadAsync 会看到已收敛而漏掉 effects。
                    Error = input.Error ?? Error;
                    StartDisposal();
                    LifecycleEntered(ActivationState.Deactivating);
                    break;
                }
                LifecycleEntered(input.Error is null ? ActivationState.Active : ActivationState.Failed);
                break;
            case PluginActivationInputKind.DependencyChanged or PluginActivationInputKind.ConfigChanged:
                HandleStale();
                break;
            case PluginActivationInputKind.Retry when State == ActivationState.Failed:
                RetryPending();
                break;
            case PluginActivationInputKind.Deactivate when State == ActivationState.Activating:
                // 卸载是终态意图: 后续 Deactivate(Unload=false) 不能把它降级成重建。
                PendingUnload |= input.Unload;
                _activationCancellation.Cancel();
                break;
            case PluginActivationInputKind.Deactivate:
                HandleDeactivate(input.Unload);
                break;
            case PluginActivationInputKind.Deactivated when State == ActivationState.Deactivating:
                LifecycleEntered(input.Unload ? ActivationState.Disposed : ActivationState.Pending);
                break;
        }
    }

    /** Active 插件依赖/配置变更 → 重建(先卸旧效应); Failed 插件 → 清错重试。 */
    private void HandleStale()
    {
        switch (State)
        {
            case ActivationState.Active:
                PendingUnload = false;
                StartDisposal();
                LifecycleEntered(ActivationState.Deactivating);
                break;
            case ActivationState.Failed:
                RetryPending();
                break;
        }
    }

    private void HandleDeactivate(bool unload)
    {
        switch (State)
        {
            case ActivationState.Pending:
                if (unload)
                    LifecycleEntered(ActivationState.Disposed);
                break;
            case ActivationState.Active:
                PendingUnload = unload;
                StartDisposal();
                LifecycleEntered(ActivationState.Deactivating);
                break;
            case ActivationState.Failed:
                LifecycleEntered(ActivationState.Disposed);
                break;
        }
    }

    private void RetryPending()
    {
        ClearError();
        LifecycleEntered(ActivationState.Pending);
    }

    private void StartDisposal()
    {
        // 不用捕获同步上下文的续体:其续体会持住本对象(进而持住插件程序集),导致协作式卸载无法回收 ALC。
        var disposal = DisposeEffectsAsync();
        _ = disposal.ContinueWith(
            _ => SendInput(new PluginActivationInput(PluginActivationInputKind.Deactivated, Unload: PendingUnload)),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    internal void LifecycleEntered(ActivationState state)
    {
        // 依赖归属必须在 Active 可见之前落定: 调度泵会在其他线程看到 Active 后立刻判定失效,
        // 若此时还是上一代提供者, 刚激活的插件会被误判为 stale 而重建。
        if (state == ActivationState.Active)
            Ctx.Root.Scheduler.CaptureDependencies(this);
        PublishState(state);
        switch (state)
        {
            case ActivationState.Active:
                Ctx.LoggerFor().Info("activated");
                Ctx.Root.Scheduler.OnActivationStateChanged();
                break;
            case ActivationState.Disposed:
                Ctx.LoggerFor().Info("deactivated");
                Ctx.Root.Scheduler.OnActivationStateChanged();
                break;
        }
    }

    /**
     * State and settle handle change together under one lock.
     * CI (macOS): AwaitTransitionsAsync saw Disposed between the state write and TrySetResult, so DeactivateAsync returned while WaitAsync was still pending.
     */
    private void PublishState(ActivationState state)
    {
        lock (_stateSync)
        {
            State = state;
            if (state is ActivationState.Activating or ActivationState.Deactivating)
            {
                // 曾出现整套测试偶发挂死在 xunit 的 Main: 它一直等某个用例收尾, 而该用例的环境组合永不返回。
                // 根因: 进入迁移态时若直接替换 _settled, AwaitTransitionsAsync 可能刚判到 IsTransitioning 就拿到这个即将被丢弃的旧句柄, 旧句柄从此再无完成者, Task.WhenAll 永不返回, SettleAsync/Compose 永久等待; 每个 GUI 用例都要 Compose, 于是整套挂住 Main。
                // 修复: 换新句柄前先 TrySetResult 释放旧句柄; AwaitTransitionsAsync 会重新评估迁移状态, 因此不会提前返回。
                (_settled, var previous) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), _settled);
                previous.TrySetResult();
            }
            else
            {
                _settled.TrySetResult();
            }
        }
    }

    internal async Task<bool> DisposeEffectsAsync()
    {
        await Effects.DisposeAllAsync(error => Ctx.Logger.Error("%s", error));
        return PendingUnload;
    }

    private async Task ActivateAsync()
    {
        var cancellationToken = ActivationToken;
        await Task.Yield();
        PluginApplyScope.Enter(Name);
        try
        {
            var result = await _definition.Apply(Ctx, Config, cancellationToken).ConfigureAwait(false);
            // Apply 返回的释放器也属于本次 activation 的效应: 先登记, 取消/卸载时才能一并撤销。
            if (result is not null)
                Effects.Add(EffectScope.Normalize(result, $"apply({Name})"));
            if (ActivationCanceled)
            {
                Error = "activation canceled";
                await Effects.DisposeAllAsync(disposeError => Ctx.Logger.Error("%s", disposeError)).ConfigureAwait(false);
                SendInput(new PluginActivationInput(PluginActivationInputKind.ActivateCompleted, Error: "activation canceled"));
                return;
            }
            SendInput(new PluginActivationInput(PluginActivationInputKind.ActivateCompleted));
        }
        catch (OperationCanceledException) when (ActivationCanceled)
        {
            Error = "activation canceled";
            await Effects.DisposeAllAsync(disposeError => Ctx.Logger.Error("%s", disposeError)).ConfigureAwait(false);
            SendInput(new PluginActivationInput(PluginActivationInputKind.ActivateCompleted, Error: "activation canceled"));
        }
        catch (Exception error)
        {
            Error = DeepestMessage(error);
            Ctx.Logger.Error("%s", error);
            // 失败前已登记的效应(服务、订阅、注册)当场撤销, 否则重试会撞上自己上一次留下的注册。
            await Effects.DisposeAllAsync(disposeError => Ctx.Logger.Error("%s", disposeError)).ConfigureAwait(false);
            SendInput(new PluginActivationInput(PluginActivationInputKind.ActivateCompleted, Error: Error));
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

}

/** 插件状态机输入(替代 LogicBlocks 输入结构, D7)。 */
public enum PluginActivationInputKind
{
    Activate,
    ActivateCompleted,
    DependencyChanged,
    ConfigChanged,
    Retry,
    Deactivate,
    Deactivated,
}

public readonly record struct PluginActivationInput(PluginActivationInputKind Kind, string? Error = null, bool Unload = false);
