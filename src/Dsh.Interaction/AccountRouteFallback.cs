using Dsh.Agent;
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
    private readonly string? _explicitProvider;
    private readonly Func<bool> _unsubscribeRequest;
    private IDisposable? _handle;
    private string? _defaultModel;

    private AccountRouteFallback(
        Context ctx,
        LlmRuntime llm,
        LlmAdapterFactoryRegistry factories,
        string? explicitProvider)
    {
        _ctx = ctx;
        _llm = llm;
        _factories = factories;
        _explicitProvider = explicitProvider;
        _unsubscribeRequest = ctx.OnWaterfall<AgentRequestNotification>(SupplyDefaultRouteAsync, new EventOptions { Global = true });
    }

    public static AccountRouteFallback? Register(Context ctx, string? explicitProvider)
    {
        var llm = ctx.Get<LlmRuntime>(LlmRuntime.ServiceName, false);
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName, false);
        if (llm is null || factories is null)
            return null;
        return new AccountRouteFallback(ctx, llm, factories, explicitProvider);
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
        _ctx.LoggerFor("llm").Info("%s",
            $"no provider with a usable credential is configured; requests default to the DeepSeek account as \"{ProviderId}\"");
        return _defaultModel;
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