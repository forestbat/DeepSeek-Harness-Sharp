using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Llm.DeepSeek.Plugin.Package)]

namespace Dsh.Llm.DeepSeek;

/** DeepSeek wire 适配器插件:登记 deepseek 工厂;默认 baseUrl 与密钥环境变量归本插件, 模型清单由端点自己下发。 */
public sealed class Plugin : IDshPlugin
{
    internal const string Package = "@deepseek-ai/dsh-llm-deepseek";

    public string[] Inject => [LlmAdapterFactoryRegistry.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName)
            ?? throw new InvalidOperationException("llmAdapterFactories is required for the DeepSeek adapter plugin");
        var homeRoot = ctx.GetProp("dshHomePath") as string ?? "";
        return factories.Register(Package, new DeepSeekAdapterFactory(homeRoot, ctx));
    }
}

internal sealed class DeepSeekAdapterFactory(string homeRoot, Context ctx) : ILlmAdapterFactory
{
    public IReadOnlyList<LlmWireDefinition> Wires { get; } =
    [
        new("deepseek",
            DefaultBaseUrl: "https://api.deepseek.com",
            DefaultApiKeyEnv: "DEEPSEEK_API_KEY",
            SelfDescribing: true),
    ];

    public LlmAdapter Create(ResolvedLlmProvider provider)
    {
        var baseUrl = Endpoint.NormalizeBaseUrl(provider.BaseUrl);
        var http = new HttpClient(OutboundProxy.FromContext(ctx).CreateHandler());
        var credential = CredentialResolver(provider, baseUrl);
        var connection = new DeepSeekConnectionOptions(
            baseUrl,
            provider.ApiKeyEnv,
            new RequestDefaults(),
            DeepSeekConnectionOptions.DefaultMaxTokens,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            provider.Models,
            new DeepSeekModelListing(baseUrl, credential, http),
            DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "llm-deepseek"));
        return new DeepSeekAdapter(provider.ProviderId, new DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveCredential = (_, cancellationToken) => credential(cancellationToken),
            RejectCredential = rejected => InferenceCredentials.Reject(ctx, rejected),
            ResolveUserId = () => AnonymousUserId.Resolve(homeRoot),
            HttpClient = http,
        });
    }

    /**
     * 凭据解析: settings 里的 API Key 优先, 否则用账号登录换来的令牌。
     * 两者走不同的请求头, 由端点自己区分; 都没有时按"缺凭据"报错, 并区分"没登录"与"key 不可用"。
     */
    private Func<CancellationToken, Task<LlmCredential>> CredentialResolver(ResolvedLlmProvider provider, string baseUrl)
        => cancellationToken =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.ApiKey is not null && ApiKey.Normalize(provider.ApiKey, out var key, out _))
                return Task.FromResult(new LlmCredential(key, LlmCredentialScheme.Bearer));
            var (accountCredential, covered) = InferenceCredentials.Resolve(ctx, baseUrl);
            if (accountCredential is not null)
                return Task.FromResult(accountCredential);
            var reason = covered
                ? $"provider \"{provider.ProviderId}\" has no API key and the DeepSeek account is not signed in"
                : $"provider \"{provider.ProviderId}\" credential \"{provider.ApiKeyEnv}\" is unusable";
            throw new LlmException(new LlmFailure(
                reason,
                covered ? LlmFailureCodes.AccountSignInRequired : LlmFailureCodes.InvalidCredential));
        };
}