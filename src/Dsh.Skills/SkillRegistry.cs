using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cordis;
using Dsh.Core;

namespace Dsh.Skills;

public sealed record SkillRegistryConfig
{
    public int CollectCacheMaxEntries { get; init; } = SkillRegistry.DefaultCollectCacheEntries;
}

public sealed class SkillRegistry : Service
{
    public const string ServiceName = "skills";
    public const string ChangeEvent = "skills/change";
    public const int BundledSkillRank = 600;
    public const int DefaultCollectCacheEntries = 128;

    private const int MaxCollectAttempts = 2;
    private const string RuntimeProvider = "runtime";
    private const int RuntimeRank = 250;

    private static readonly Regex SkillNamePattern = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);

    private sealed class ProviderSlot(ISkillProvider provider, int order)
    {
        public ISkillProvider Provider { get; } = provider;
        public int Order { get; } = order;
        public bool Live { get; set; } = true;
    }

    private sealed class SkillLayer
    {
        public NamedEntries<ProviderSlot> Providers { get; }

        public Dictionary<string, SkillDefinition> Runtime { get; } = [];

        public SkillLayer(ScopeKey? scope)
        {
            Providers = new NamedEntries<ProviderSlot>(name => new InvalidOperationException(scope is null
                ? $"a skill provider named \"{name}\" is already registered"
                : $"a skill provider named \"{name}\" is already registered in this scope"));
        }
    }

    private sealed record IndexedCandidate(SkillCandidate Candidate, ISkillProvider Provider, int ProviderOrder, int LocalOrder, ProviderSlot? Slot);

    private sealed record CollectResult(Dictionary<string, IndexedCandidate> Entries, bool Cacheable);

    private sealed class ScopeIdBox
    {
        public int Value;
    }

    private readonly ScopedLayers<SkillLayer> _layers;
    private readonly Dictionary<string, Dictionary<string, IndexedCandidate>> _collectCache = [];
    private readonly ConditionalWeakTable<ScopeKey, ScopeIdBox> _scopeIds = new();
    private readonly int _collectCacheMaxEntries;
    private int _revision;
    private int _nextProviderOrder;
    private int _nextScopeId = 1;

    public SkillRegistry(Context ctx, SkillRegistryConfig? config = null) : base(ctx, ServiceName)
    {
        _collectCacheMaxEntries = config?.CollectCacheMaxEntries ?? DefaultCollectCacheEntries;
        if (_collectCacheMaxEntries < 1)
            throw new ArgumentException($"skill: {nameof(SkillRegistryConfig.CollectCacheMaxEntries)} must be an integer greater than or equal to 1");
        _layers = new ScopedLayers<SkillLayer>(scope => new SkillLayer(scope), InvalidateCache);
    }

    public static bool IsSkillName(string name) => SkillNamePattern.IsMatch(name);

    public IDisposable RegisterProvider(Func<SkillProviderControl, ISkillProvider> create)
    {
        var lifecycle = new CancellationTokenSource();
        ProviderSlot? slot = null;
        var control = new SkillProviderControl(lifecycle.Token, () =>
        {
            if (slot is { Live: true })
                InvalidateCache();
        });
        ISkillProvider provider;
        try
        {
            provider = create(control);
        }
        catch
        {
            lifecycle.Cancel();
            throw;
        }
        var name = provider.Name;
        if (name == RuntimeProvider)
        {
            lifecycle.Cancel();
            throw new InvalidOperationException($"\"{RuntimeProvider}\" is reserved for runtime skill registrations");
        }
        slot = new ProviderSlot(provider, _nextProviderOrder++);
        try
        {
            return _layers.Effect(Ctx, null,
                layer => layer.Providers.Insert(name, slot),
                layer =>
                {
                    slot.Live = false;
                    layer.Providers.Remove(name);
                    lifecycle.Cancel();
                });
        }
        catch
        {
            lifecycle.Cancel();
            throw;
        }
    }

    public IDisposable Register(SkillRegistration skill)
    {
        ValidateRuntimeSkill(skill);
        var scope = DshScope.ScopeOf(Ctx);
        var existingLayer = scope is null ? _layers.Global : _layers.Peek(scope);
        if (existingLayer is not null && existingLayer.Runtime.ContainsKey(skill.Name))
        {
            Ctx.LoggerFor(ServiceName).Warn($"runtime skill \"{skill.Name}\" ignored because it is already registered");
            return new NullDisposable();
        }
        var definition = new SkillDefinition
        {
            Name = skill.Name,
            Description = skill.Description,
            WhenToUse = skill.WhenToUse,
            Invocation = skill.Invocation ?? new SkillInvocationPolicy(true, true),
            Source = skill.Source,
            Provider = skill.Provider ?? RuntimeProvider,
            ResourceBase = skill.ResourceBase,
            Content = skill.Content,
            Path = skill.Path,
            Metadata = skill.Metadata,
        };
        return _layers.Effect(Ctx, scope,
            layer => layer.Runtime[definition.Name] = definition,
            layer => layer.Runtime.Remove(definition.Name));
    }

    public async Task<IReadOnlyList<SkillSummary>> List(SkillViewOptions? options = null)
        => (await Snapshot(options)).Skills;

    public async Task<SkillCatalogSnapshot> Snapshot(SkillViewOptions? options = null)
    {
        var collected = await Collect(options ?? new SkillViewOptions());
        var skills = collected.Entries.Values
            .Select(entry => (SkillSummary)entry.Candidate)
            .OrderBy(summary => summary.Name, StringComparer.Ordinal)
            .ToList();
        return new SkillCatalogSnapshot(skills, collected.Cacheable);
    }

    public async Task<SkillDefinition?> Get(string name, SkillViewOptions? options = null)
    {
        if (!IsSkillName(name))
            return null;
        options ??= new SkillViewOptions();
        var collected = await Collect(options);
        options.Signal.ThrowIfCancellationRequested();
        if (!collected.Entries.TryGetValue(name, out var match))
            return null;
        var definition = await WaitWithAbort(match.Provider.Get(match.Candidate, options), options.Signal);
        if (definition is null)
            return null;
        ValidateDefinition(definition);
        if (definition.Name != match.Candidate.Name)
        {
            if (match.Slot is { Live: true })
                InvalidateCache();
            return null;
        }
        return definition;
    }

    private async Task<CollectResult> Collect(SkillViewOptions options)
    {
        options.Signal.ThrowIfCancellationRequested();
        var attempt = 1;
        while (true)
        {
            var revision = _revision;
            var key = CollectCacheKey(options.Cwd, options.Scope, revision);
            if (_collectCache.TryGetValue(key, out var cached))
                return new CollectResult(cached, true);

            var result = await CollectFresh(options);
            options.Signal.ThrowIfCancellationRequested();
            if (revision != _revision)
            {
                if (attempt < MaxCollectAttempts)
                {
                    attempt += 1;
                    continue;
                }
                return result with { Cacheable = false };
            }
            if (result.Cacheable)
            {
                _collectCache[key] = result.Entries;
                if (_collectCache.Count > _collectCacheMaxEntries)
                    _collectCache.Remove(_collectCache.Keys.First());
            }
            return result;
        }
    }

    private async Task<CollectResult> CollectFresh(SkillViewOptions options)
    {
        var layers = new List<SkillLayer> { _layers.Global };
        layers.AddRange(_layers.ChainLayers(options.Scope));
        var merged = new Dictionary<string, IndexedCandidate>();
        var cacheable = true;
        foreach (var layer in layers)
        {
            var collected = await CollectLayer(layer, options);
            if (!collected.Cacheable)
                cacheable = false;
            foreach (var entry in collected.Entries)
                merged[entry.Candidate.Name] = entry;
        }
        return new CollectResult(merged, cacheable);
    }

    private async Task<(List<IndexedCandidate> Entries, bool Cacheable)> CollectLayer(SkillLayer layer, SkillLookupOptions options)
    {
        var collected = await ListLayerCandidates(layer, options);
        collected.Entries.Sort((left, right) =>
            left.Candidate.Rank != right.Candidate.Rank ? left.Candidate.Rank - right.Candidate.Rank
            : left.ProviderOrder != right.ProviderOrder ? left.ProviderOrder - right.ProviderOrder
            : left.LocalOrder - right.LocalOrder);
        var seen = new HashSet<string>();
        var result = new List<IndexedCandidate>();
        foreach (var entry in collected.Entries)
        {
            var skill = entry.Candidate;
            if (!seen.Add(skill.Name))
            {
                Ctx.LoggerFor(ServiceName).Warn($"skill \"{skill.Name}\" from {skill.Source} ignored because a higher-priority skill already exists");
                continue;
            }
            result.Add(entry);
        }
        return (result, collected.Cacheable);
    }

    private async Task<(List<IndexedCandidate> Entries, bool Cacheable)> ListLayerCandidates(SkillLayer layer, SkillLookupOptions options)
    {
        options.Signal.ThrowIfCancellationRequested();
        var candidates = new List<IndexedCandidate>();
        var cacheable = true;
        var runtimeOrder = 0;
        foreach (var skill in layer.Runtime.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal))
        {
            candidates.Add(new IndexedCandidate(RuntimeCandidate(skill), RuntimeSkillProvider.Instance, -1, runtimeOrder, null));
            runtimeOrder += 1;
        }
        foreach (var (_, slot) in layer.Providers.Entries)
        {
            var localOrder = 0;
            SkillProviderObservation observation;
            try
            {
                observation = await WaitWithAbort(slot.Provider.List(options), options.Signal);
            }
            catch (Exception error)
            {
                if (options.Signal.IsCancellationRequested)
                    throw;
                cacheable = false;
                Ctx.LoggerFor(ServiceName).Warn($"skill provider \"{slot.Provider.Name}\" skipped: {error.Message}");
                continue;
            }
            if (!observation.Complete)
                cacheable = false;
            foreach (var candidate in observation.Candidates)
            {
                ValidateCandidate(candidate, slot.Provider.Name);
                candidates.Add(new IndexedCandidate(candidate, slot.Provider, slot.Order, localOrder, slot));
                localOrder += 1;
            }
        }
        return (candidates, cacheable);
    }

    private void InvalidateCache()
    {
        _revision += 1;
        _collectCache.Clear();
        Ctx.Emit(ChangeEvent);
    }

    private int ScopeId(ScopeKey key)
        => _scopeIds.GetValue(key, _ => new ScopeIdBox { Value = _nextScopeId++ }).Value;

    private string CollectCacheKey(string? cwd, ScopeKey? scope, int revision)
    {
        var scopes = scope is null
            ? []
            : DshScope.ScopeChainOf(scope).Select(ScopeId).ToList();
        return JsonSerializer.Serialize(new { cwd, scopes, revision });
    }

    private static async Task<T> WaitWithAbort<T>(Task<T> task, CancellationToken signal)
    {
        if (!signal.CanBeCanceled)
            return await task;
        signal.ThrowIfCancellationRequested();
        var cancelled = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = signal.Register(
            static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
            cancelled);
        var completed = await Task.WhenAny(task, cancelled.Task);
        if (completed == cancelled.Task)
            throw new OperationCanceledException(signal);
        return await task;
    }

    private static SkillCandidate RuntimeCandidate(SkillDefinition skill)
        => new()
        {
            Name = skill.Name,
            Description = skill.Description,
            WhenToUse = skill.WhenToUse,
            Invocation = skill.Invocation,
            Source = skill.Source,
            Provider = skill.Provider,
            ResourceBase = skill.ResourceBase,
            Rank = RuntimeRank,
            Locator = skill,
            Path = skill.Path,
            Metadata = skill.Metadata,
        };

    private static void ValidateCandidate(SkillCandidate candidate, string providerName)
    {
        if (!IsSkillName(candidate.Name))
            throw new InvalidOperationException($"skill provider \"{providerName}\" returned invalid skill name \"{candidate.Name}\"");
        if (candidate.Description.Length == 0)
            throw new InvalidOperationException($"skill provider \"{providerName}\" returned skill \"{candidate.Name}\" without a description");
        if (candidate.Provider != providerName)
            throw new InvalidOperationException($"skill provider \"{providerName}\" returned skill \"{candidate.Name}\" for provider \"{candidate.Provider}\"");
    }

    private static void ValidateRuntimeSkill(SkillRegistration skill)
    {
        if (!IsSkillName(skill.Name))
            throw new ArgumentException($"invalid skill name \"{skill.Name}\"");
        if (skill.Description.Length == 0)
            throw new ArgumentException($"skill \"{skill.Name}\" requires a description");
    }

    private static void ValidateDefinition(SkillDefinition skill)
    {
        if (!IsSkillName(skill.Name))
            throw new InvalidOperationException($"loaded skill has invalid name \"{skill.Name}\"");
        if (skill.Description.Length == 0)
            throw new InvalidOperationException($"loaded skill \"{skill.Name}\" requires a description");
    }

    private sealed class RuntimeSkillProvider : ISkillProvider
    {
        public static readonly RuntimeSkillProvider Instance = new();

        public string Name => RuntimeProvider;

        public Task<SkillProviderObservation> List(SkillLookupOptions options)
            => Task.FromResult(SkillProviderObservation.Full([]));

        public Task<SkillDefinition?> Get(SkillCandidate candidate, SkillLookupOptions options)
            => Task.FromResult((SkillDefinition?)candidate.Locator);
    }

    private sealed class NullDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
