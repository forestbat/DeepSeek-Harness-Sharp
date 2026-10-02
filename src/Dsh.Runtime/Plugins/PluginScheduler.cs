namespace Dsh.Runtime;

/** 插件调度器:维护 服务名→提供者 与 插件→Inject 依赖,驱动状态机,承载重建 (i) 与协作式卸载。 */
public sealed class PluginScheduler
{
    public static readonly TimeSpan DefaultUnloadTimeout = TimeSpan.FromSeconds(10);

    private const int MaxPasses = 16;

    private readonly Context _root;
    private readonly Lock _sync = new();
    private readonly List<PluginActivation> _activations = [];
    private readonly Dictionary<string, PluginActivation> _providers = new(StringComparer.Ordinal);
    private readonly Func<PluginActivation, bool> _canActivate;
    /** 注意: `TryRequestRebuildExclusive` 的入参是"依赖仍然新鲜"判据(取反后的 IsStale), 别传成 IsStale。 */
    private readonly Func<PluginActivation, bool> _dependencyFresh;
    private Dictionary<string, PluginActivation>? _providerSnapshot;
    private long _providersVersion;
    private long _providerSnapshotVersion = -1;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task _chain = Task.CompletedTask;
    private long _epoch;

    public PluginScheduler(Context root)
    {
        _root = root;
        // 解析循环里每次评估都会用到这两个委托: 缓存起来, 免得每轮新建闭包(实测启动期 ~3000 次分配)。
        _canActivate = CanActivate;
        _dependencyFresh = activation => !IsStale(activation);
    }

    public long Epoch
    {
        get
        {
            lock (_sync)
                return _epoch;
        }
    }

    public IReadOnlyList<PluginActivation> Snapshot()
    {
        lock (_sync)
            return [.. _activations];
    }

    public PluginActivation? Find(string name)
    {
        lock (_sync)
            return _activations.FirstOrDefault(activation => string.Equals(activation.Name, name, StringComparison.Ordinal));
    }

    public PluginActivation Register(PluginDefinition definition, object? config)
    {
        var activation = new PluginActivation(definition, config);
        activation.Attach(_root.CreatePluginContext(activation));
        lock (_sync)
        {
            _activations.Add(activation);
            _epoch++;
        }
        TryActivate(activation);
        Enqueue();
        return activation;
    }

    public async Task<PluginActivation> AddAsync(PluginDefinition definition, object? config = null)
    {
        var activation = Register(definition, config);
        await SettleAsync();
        return activation;
    }

    /** 显式收敛:排空后台泵并激活可用者、重建依赖失配者,直至稳定。 */
    public async Task SettleAsync()
    {
        for (var round = 0; round < MaxPasses; round++)
        {
            Task chain;
            lock (_sync)
                chain = _chain;
            await chain.ConfigureAwait(false);
            await RunExclusiveAsync().ConfigureAwait(false);
            lock (_sync)
            {
                if (ReferenceEquals(chain, _chain))
                    return;
            }
        }
    }

    private async Task RunExclusiveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RunPassesAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /** 协作式卸载:逆序停依赖方 → 等待被卸载插件 settle(默认超时仅作安全阀)。 */
    public async Task<PluginActivation?> UnloadAsync(string name, TimeSpan? timeout = null, bool force = false)
    {
        var activation = Find(name);
        if (activation is null)
            return null;
        foreach (var dependent in DependentsOf(activation).AsEnumerable().Reverse())
        {
            if (dependent.State is ActivationState.Active or ActivationState.Failed)
                dependent.RequestRebuild();
        }
        activation.RequestUnload();
        if (!force)
        {
            try
            {
                await AwaitTransitionsAsync(timeout ?? DefaultUnloadTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
        lock (_sync)
        {
            _activations.Remove(activation);
            foreach (var provided in activation.ProvidedNames)
            {
                if (_providers.TryGetValue(provided, out var owner) && ReferenceEquals(owner, activation))
                {
                    _providers.Remove(provided);
                    _providersVersion++;
                }
            }
        }
        Enqueue();
        return activation;
    }

    public void OnServiceProvided(string name, PluginActivation owner)
    {
        PluginActivation? previous;
        lock (_sync)
        {
            _providers.TryGetValue(name, out previous);
            _providers[name] = owner;
            _providersVersion++;
            _epoch++;
        }
        if (previous is not null && !ReferenceEquals(previous, owner))
            RebuildDependentsOf(name);
        Enqueue();
    }

    public void OnServiceRemoved(string name, PluginActivation owner)
    {
        lock (_sync)
        {
            if (_providers.TryGetValue(name, out var current) && ReferenceEquals(current, owner))
            {
                _providers.Remove(name);
                _providersVersion++;
                _epoch++;
            }
        }
        Enqueue();
    }

    internal void OnActivationStateChanged(PluginActivation activation, ActivationState state)
    {
        if (state == ActivationState.Active)
        {
            Dictionary<string, PluginActivation> providers;
            lock (_sync)
                providers = new Dictionary<string, PluginActivation>(_providers, StringComparer.Ordinal);
            activation.CaptureDependencies(providers);
        }
        Enqueue();
    }

    /**
     * _providers 的只读副本: 只在提供者表真的变过时重建。
     * 激活资格与失效判定在解析循环里高频调用(实测 GUI 启动期 1294 次整表复制 ~131MB 分配), 必须复用。
     */
    private Dictionary<string, PluginActivation> ProviderSnapshot()
    {
        lock (_sync)
        {
            if (_providerSnapshot is null || _providerSnapshotVersion != _providersVersion)
            {
                _providerSnapshot = new Dictionary<string, PluginActivation>(_providers, StringComparer.Ordinal);
                _providerSnapshotVersion = _providersVersion;
            }

            return _providerSnapshot;
        }
    }

    private bool CanActivate(PluginActivation activation) => activation.DependenciesAvailable(ProviderSnapshot());

    private bool IsStale(PluginActivation activation) => activation.IsStale(ProviderSnapshot());

    /** 等待所有进行中的迁移落地; 返回值表示本轮是否真有迁移被等到(调用方据此决定是否再评估一轮)。 */
    public async Task<bool> AwaitTransitionsAsync(TimeSpan? timeout = null)
    {
        var deadline = timeout is { } limit ? DateTime.UtcNow + limit : (DateTime?)null;
        var awaited = false;
        while (true)
        {
            var waits = Snapshot()
                .Where(activation => activation.IsTransitioning)
                .Select(activation => activation.WaitAsync())
                .ToArray();
            if (waits.Length == 0)
                return awaited;
            awaited = true;
            var remaining = deadline is { } until ? until - DateTime.UtcNow : (TimeSpan?)null;
            if (remaining is { } left && left <= TimeSpan.Zero)
                throw new TimeoutException("plugin transitions did not settle in time");
            var all = Task.WhenAll(waits);
            await (remaining is { } wait ? all.WaitAsync(wait) : all).ConfigureAwait(false);
        }
    }

    private void Enqueue()
    {
        lock (_sync)
        {
            var pump = PumpAfterAsync(_chain);
            _chain = pump;
            _ = pump.ContinueWith(
                _ =>
                {
                    lock (_sync)
                    {
                        if (ReferenceEquals(_chain, pump))
                            _chain = Task.CompletedTask;
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task PumpAfterAsync(Task previous)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await RunExclusiveAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _root.Logger.Error("%s", error);
        }
    }

    private async Task RunPassesAsync()
    {
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var changed = RebuildStaleDependents();
            changed |= ActivateEligible();
            // 迁移(如 Deactivating→Pending)在等待期间才落地: 新 Pending 的激活本轮尚未评估, 必须再跑一轮
            if (await AwaitTransitionsAsync().ConfigureAwait(false))
                changed = true;
            if (!changed)
                break;
        }
    }

    private bool RebuildStaleDependents()
    {
        var changed = false;
        var snapshot = Snapshot();
        for (var index = snapshot.Count - 1; index >= 0; index--)
        {
            var activation = snapshot[index];
            if (activation.TryRequestRebuildExclusive(_dependencyFresh))
                changed = true;
        }
        return changed;
    }

    private bool ActivateEligible()
    {
        var changed = false;
        foreach (var activation in Snapshot())
        {
            if (TryActivate(activation))
                changed = true;
        }
        return changed;
    }

    private bool TryActivate(PluginActivation activation)
    {
        if (!activation.TryStartExclusive(_canActivate))
            return false;
        lock (_sync)
            _epoch++;
        return true;
    }

    private void RebuildDependentsOf(string serviceName)
    {
        foreach (var dependent in DependentsOfProvider(serviceName).AsEnumerable().Reverse())
        {
            if (dependent.State is ActivationState.Active or ActivationState.Failed)
                dependent.RequestRebuild();
        }
    }

    private List<PluginActivation> DependentsOf(PluginActivation provider)
    {
        var services = provider.ProvidedNames;
        var direct = Snapshot()
            .Where(candidate => !ReferenceEquals(candidate, provider) && services.Any(candidate.DependsOn))
            .ToList();
        return Expand(direct);
    }

    private List<PluginActivation> DependentsOfProvider(string serviceName)
    {
        var direct = Snapshot()
            .Where(candidate => candidate.DependsOn(serviceName))
            .ToList();
        return Expand(direct);
    }

    /** 自直接依赖方按 BFS 展开传递闭包:返回提供者优先序,逆序即依赖方优先。 */
    private List<PluginActivation> Expand(List<PluginActivation> direct)
    {
        var snapshot = Snapshot();
        var seen = new HashSet<PluginActivation>(direct);
        var ordered = new List<PluginActivation>(direct);
        var queue = new Queue<PluginActivation>(direct);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var services = current.ProvidedNames;
            foreach (var candidate in snapshot)
            {
                if (seen.Contains(candidate))
                    continue;
                if (services.Any(candidate.DependsOn))
                {
                    seen.Add(candidate);
                    ordered.Add(candidate);
                    queue.Enqueue(candidate);
                }
            }
        }
        return ordered;
    }
}
