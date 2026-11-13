using Dsh.Agent;
using Dsh.Boot;
using Dsh.Llm;
using Dsh.Runtime;
using Dsh.Runtime.Events;

namespace Dsh.Interaction;

/**
 * 账号兜底路由: settings.yaml 里没有任何可用 provider 时(零配置, 或只登录了 DeepSeek 账号), 用账号令牌开一条 provider。
 * 只在"用户没有自己的模型可用"时生效: 有任何一条注册成功的 provider 就不介入, 也不写回 settings.yaml。
 * 模型取自端点 /models(账号当前能用的模型), 因此不需要用户手抄模型 id。
 */
public sealed class AccountRouteFallback : IDisposable
{
    /** 兜底 provider 的名字: 模板里的官方条目叫 deepseek-official, 这条专指"账号登录"。 */
    public const string ProviderId = "deepseek-account";

    /** 首轮注册时问一次端点清单的上限: 拿不到就不兜底, 不把空模型发给请求。 */
    private static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(10);

    private static readonly HttpClient ListingHttp = new();

    private readonly Context _ctx;
    private readonly LlmRuntime _llm;
    private readonly LlmAdapterFactoryRegistry _factories;
    private readonly HarnessHome _home;
    private readonly string? _explicitProvider;
    private readonly Func<bool> _unsubscribeRequest;
    private IDisposable? _handle;
    private string? _defaultModel;

    private AccountRouteFallback(
        Context ctx,
        LlmRuntime llm,
        LlmAdapterFactoryRegistry factories,
        HarnessHome home,
        string? explicitProvider)
    {
        _ctx = ctx;
        _llm = llm;
        _factories = factories;
        _home = home;
        _explicitProvider = explicitProvider;
        _unsubscribeRequest = ctx.OnWaterfall<AgentRequestNotification>(SupplyDefaultRouteAsync, new EventOptions { Global = true });
    }

    public static AccountRouteFallback? Register(Context ctx, HarnessHome home, string? explicitProvider)
    {
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName, false);
        if (llm is null || factories is null)
            return null;
        return new AccountRouteFallback(ctx, llm, factories, home, explicitProvider);
    }

    /** 用户自己配了 provider, 或有 CLI 指定的 provider: 一律不兜底。 */
    public void Sync(bool anyProviderRegistered)
    {
        if (anyProviderRegistered)
            Release();
    }

    /**
     * 会话没有 provider/model 时补上兜底路由。这里才注册 provider:
     * 账号可能是在进程启动之后才登录的, 提前注册只会拿到一个"当时还没登录"的判断。
     */
    private async ValueTask<object?> SupplyDefaultRouteAsync(AgentRequestNotification notification, Func<ValueTask<object?>> next)
    {
        var proposed = await next().ConfigureAwait(false);
        if (proposed is not LlmCallConfig config
            || (config.Provider.Length > 0 && config.Model.Length > 0)
            || _explicitProvider is not null)
        {
            return proposed;
        }
        if (await EnsureRegisteredAsync(notification.Payload.Signal).ConfigureAwait(false) is not { } model)
            return proposed;
        return config with { Provider = ProviderId, Model = model };
    }

    /** 账号可用且没有任何其它 provider 时注册兜底路由, 并返回端点清单里的第一个模型。 */
    private async Task<string?> EnsureRegisteredAsync(CancellationToken cancellationToken)
    {
        if (InferenceCredentials.Origin(_ctx) is not { } origin
            || InferenceCredentials.Resolve(_ctx, origin.GetLeftPart(UriPartial.Authority)).Credential is not { } credential)
        {
            return null;
        }
        if (_handle is not null)
            return _defaultModel;
        if (_llm.ListProviders().Count > 0)
            return null;
        if (!_factories.TryResolve(ProviderTypes.DeepSeek, out _, out var factory))
            return null;
        var ids = await FetchModelIdsAsync(origin, credential, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
            return null;
        // 清单里的模型作为声明一起注册: 之后会话用哪个由清单决定, 不需要适配器再等一次网络。
        var provider = new ResolvedLlmProvider(
            ProviderId,
            ProviderTypes.DeepSeek,
            origin.GetLeftPart(UriPartial.Authority),
            "DEEPSEEK_API_KEY",
            ApiKey: null,
            Models: [.. ids.Select(id => new ProviderModelSpec(id))],
            ApiStyle: null);
        _handle = _llm.RegisterAdapter([ProviderId], factory!.Create(provider));
        _defaultModel = ids[0];
        Persist(origin, ids);
        _ctx.LoggerFor("llm").Info("%s",
            $"no provider with a usable credential is configured; requests default to the DeepSeek account as \"{ProviderId}\"");
        return _defaultModel;
    }

    /**
     * 把兜底路由写进 settings.yaml: 下次启动直接按配置注册, 不必再为"用哪个模型"问一次端点。
     * 只在这条记录不存在或模型清单变了时写, 不动用户自己的 provider 与已有的其它设置。
     */
    private void Persist(Uri origin, IReadOnlyList<string> ids)
    {
        try
        {
            var settings = HarnessSettings.Load(_home);
            var existing = settings.Providers.GetValueOrDefault(ProviderId);
            var models = existing?.Models ?? [];
            var sameModels = models.Count == ids.Count && ids.All(id => models.ContainsKey(id));
            if (existing is not null && sameModels)
                return;
            var merged = new Dictionary<string, ProviderModelSettings>(StringComparer.Ordinal);
            foreach (var id in ids)
                merged[id] = models.GetValueOrDefault(id) ?? new ProviderModelSettings();
            settings.Providers[ProviderId] = new ProviderSettings
            {
                Type = ProviderTypes.DeepSeek,
                Options = new ProviderOptions { BaseUrl = origin.GetLeftPart(UriPartial.Authority) },
                Models = merged,
            };
            settings.GlobalDefaultModel ??= $"{ProviderId}/{ids[0]}";
            settings.Save(_home);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 写不进去只影响下次启动要重新问一次端点, 不改变本次会话已经生效的路由。
            _ctx.LoggerFor("llm").Warn("%s", $"failed to persist the DeepSeek account route: {error.Message}");
        }
    }

    /** 端点清单只在这里问一次: 账号当前能用哪些模型, 由服务端说了算。 */
    private static async Task<IReadOnlyList<string>> FetchModelIdsAsync(Uri origin, LlmCredential credential, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ListingTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{origin.GetLeftPart(UriPartial.Authority)}/models");
        credential.Apply(request);
        request.Headers.TryAddWithoutValidation("user-agent", AppIdentity.Default.UserAgent);
        try
        {
            using var response = await ListingHttp.SendAsync(request, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return [];
            var json = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            return EndpointModelList.ParseIds(json);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            return [];
        }
    }

    private void Release()
    {
        _handle?.Dispose();
        _handle = null;
        _defaultModel = null;
    }

    public void Dispose()
    {
        _unsubscribeRequest();
        Release();
    }
}