using Dsh.Runtime;

namespace Dsh.Web;

[global::Dsh.Plugins.DshPluginSettings]
public sealed record WebRuntimeConfig(string? SearchProvider = null, string? FetchProvider = null);

public sealed class WebRuntime : Service
{
    public const string ServiceName = "web";

    private readonly Dictionary<string, IWebSearchProvider> _searchProviders = [];
    private readonly Dictionary<string, IWebFetchProvider> _fetchProviders = [];
    private readonly Lock _sync = new();
    private readonly string? _searchProviderId;
    private readonly string? _fetchProviderId;

    public WebRuntime(Context ctx, WebRuntimeConfig? config = null) : base(ctx, ServiceName)
    {
        var resolved = config ?? new WebRuntimeConfig();
        _searchProviderId = resolved.SearchProvider;
        _fetchProviderId = resolved.FetchProvider;
    }

    public static IDisposable Register(Context ctx, WebRuntimeConfig? config = null)
    {
        _ = new WebRuntime(ctx, config ?? new WebRuntimeConfig());
        return new EffectHandleDisposable(ctx.Effect(() => (Action)(() => { }), "web"));
    }

    public static IDisposable Apply(Context ctx, object? config = null)
    {
        var resolved = config switch
        {
            WebRuntimeConfig typed => typed,
            null => new WebRuntimeConfig(),
            IReadOnlyDictionary<string, object?> dict => new WebRuntimeConfig(
                dict.GetValueOrDefault("searchProvider") as string,
                dict.GetValueOrDefault("fetchProvider") as string),
            _ => throw new ArgumentException("web config must be an object or WebRuntimeConfig", nameof(config)),
        };
        return Register(ctx, resolved);
    }

    public IDisposable RegisterSearchProvider(IWebSearchProvider provider)
        => RegisterProvider(_searchProviders, provider);

    public IDisposable RegisterFetchProvider(IWebFetchProvider provider)
        => RegisterProvider(_fetchProviders, provider);

    public async Task<WebSearchResult> Search(WebSearchRequest request, CancellationToken signal = default)
    {
        var provider = request.Engine is { } engine
            ? ResolveNamedEngine(engine)
            : ResolveProvider(_searchProviders, _searchProviderId, candidate => candidate.ImplicitCandidate);
        var result = await provider.Search(request, signal).ConfigureAwait(false);
        return CapSources(result with { Engine = provider.Id }, request.MaxResults);
    }

    /** Usable search providers ordered by id, for prompts and error hints. */
    public IReadOnlyList<IWebSearchProvider> SearchEngines()
    {
        IWebSearchProvider[] snapshot;
        lock (_sync)
            snapshot = [.. _searchProviders.Values];
        return snapshot.Where(provider => provider.Available()).OrderBy(provider => provider.Id, StringComparer.Ordinal).ToList();
    }

    public Task<WebFetchResult> Fetch(WebFetchRequest request, CancellationToken signal = default)
    {
        var provider = ResolveProvider(_fetchProviders, _fetchProviderId, _ => true);
        return provider.Fetch(request, signal);
    }

    private IDisposable RegisterProvider<T>(Dictionary<string, T> store, T provider) where T : IWebProvider
    {
        lock (_sync)
        {
            if (store.ContainsKey(provider.Id))
            {
                throw new WebError($"a web provider with id \"{provider.Id}\" is already registered", WebErrorCodes.DuplicateProvider);
            }
        }
        return new EffectHandleDisposable(Ctx.Effect(() =>
        {
            lock (_sync)
                store[provider.Id] = provider;
            return (Action)(() =>
            {
                lock (_sync)
                    store.Remove(provider.Id);
            });
        }, "web.registerProvider()"));
    }

    private IWebSearchProvider ResolveNamedEngine(string engine)
    {
        IWebSearchProvider? named;
        lock (_sync)
            _searchProviders.TryGetValue(engine, out named);
        if (named is not null && named.Available())
            return named;
        var state = named is null ? "is not registered" : $"is registered but unavailable ({named.Description})";
        var usable = string.Join(", ", SearchEngines().Select(provider => provider.Id));
        throw new WebError($"search engine \"{engine}\" {state}; usable engines: {(usable.Length > 0 ? usable : "none")}", WebErrorCodes.InvalidArgs);
    }

    private T ResolveProvider<T>(Dictionary<string, T> providers, string? configuredId, Func<T, bool> implicitCandidate) where T : IWebProvider
    {
        T[] snapshot;
        lock (_sync)
            snapshot = [.. providers.Values];
        if (configuredId is not null)
        {
            var configured = snapshot.FirstOrDefault(provider => string.Equals(provider.Id, configuredId, StringComparison.Ordinal));
            if (configured is null)
            {
                throw new WebError($"configured web provider \"{configuredId}\" is not registered", WebErrorCodes.ProviderConfiguredMissing);
            }
            if (!configured.Available())
            {
                throw new WebError($"configured web provider \"{configuredId}\" is registered but unavailable", WebErrorCodes.ProviderConfiguredUnavailable);
            }
            return configured;
        }

        var usable = snapshot.Where(provider => implicitCandidate(provider) && provider.Available()).ToList();
        if (usable.Count == 0)
        {
            var named = string.Join(", ", snapshot.Where(provider => !implicitCandidate(provider) && provider.Available()).Select(provider => provider.Id));
            var hint = named.Length > 0 ? $"; name one of these engines instead: {named}" : "";
            throw new WebError($"no usable web provider is registered{hint}", WebErrorCodes.ProviderUnavailable);
        }
        if (usable.Count > 1)
        {
            var ids = string.Join(", ", usable.Select(provider => provider.Id));
            throw new WebError($"multiple usable web providers are registered ({ids}); configure one explicitly", WebErrorCodes.ProviderAmbiguous);
        }
        return usable[0];
    }

    private static WebSearchResult CapSources(WebSearchResult result, int? maxResults)
    {
        if (maxResults is null || result.Sources.Count <= maxResults.Value)
            return result;
        return result with { Sources = result.Sources.Take(maxResults.Value).ToList(), Truncated = true };
    }
}
