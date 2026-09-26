using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Llm.DeepSeek.Plugin.Package)]

namespace Dsh.Llm.DeepSeek;

/** DeepSeek wire 适配器插件:登记 deepseek 工厂;默认 baseUrl/密钥环境变量与模型目录都归本插件。 */
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
    private static readonly IReadOnlyList<DeepSeekCatalogModel> Catalog =
    [
        new("deepseek-v4-flash", "DeepSeek-V4-Flash",
            "Fast, efficient, and economical; suited to focused, routine, or parallel tasks.",
            DeepSeekConnectionOptions.DefaultContextWindowValue),
        new("deepseek-v4-pro", "DeepSeek-V4-Pro",
            "Stronger agentic coding, knowledge, and reasoning; suited to complex or quality-critical tasks at higher cost.",
            DeepSeekConnectionOptions.DefaultContextWindowValue),
        new("deepseek-v4-flash-vision-exp", "DeepSeek-V4-Flash-Vision-Exp",
            null,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            null,
            ["text", "image"]),
    ];

    public IReadOnlyList<LlmWireDefinition> Wires { get; } =
    [
        new("deepseek",
            DefaultBaseUrl: "https://api.deepseek.com",
            DefaultApiKeyEnv: "DEEPSEEK_API_KEY"),
    ];

    public LlmAdapter Create(ResolvedLlmProvider provider)
    {
        var connection = new DeepSeekConnectionOptions(
            Endpoint.NormalizeBaseUrl(provider.BaseUrl),
            provider.ApiKeyEnv,
            new RequestDefaults(),
            DeepSeekConnectionOptions.DefaultMaxTokens,
            DeepSeekConnectionOptions.DefaultContextWindowValue,
            MergeCatalog(provider.Models),
            DeepSeekConnectionOptions.DefaultStreamIdleTimeoutMs,
            ResolvedRetryPolicy.Resolve(null, "llm-deepseek"));
        return new DeepSeekAdapter(provider.ProviderId, new DeepSeekAdapterOptions
        {
            Options = () => connection,
            ResolveApiKey = (activeConnection, cancellationToken) =>
            {
                if (provider.ApiKey is not null && ApiKey.Normalize(provider.ApiKey, out var key, out _))
                    return Task.FromResult(key);
                var (accountToken, accountCovered) = InferenceCredentials.Resolve(ctx, activeConnection.BaseUrl);
                if (accountToken is { Length: > 0 })
                    return Task.FromResult(accountToken);
                var reason = accountCovered
                    ? $"provider \"{provider.ProviderId}\" has no API key and the DeepSeek account is not signed in"
                    : $"provider \"{provider.ProviderId}\" credential \"{provider.ApiKeyEnv}\" is unusable";
                throw new LlmException(new LlmFailure(reason, LlmFailureCodes.InvalidCredential));
            },
            ResolveUserId = () => AnonymousUserId.Resolve(homeRoot),
        });
    }

    private static IReadOnlyList<DeepSeekCatalogModel> MergeCatalog(IReadOnlyList<ProviderModelSpec> models)
    {
        if (models.Count == 0)
            return Catalog;
        var merged = Catalog.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var model in models)
        {
            merged.TryGetValue(model.Id, out var existing);
            merged[model.Id] = new DeepSeekCatalogModel(
                model.Id,
                model.Name ?? existing?.Name,
                existing?.Description,
                existing?.ContextWindow,
                existing?.MaxTokens,
                existing?.InputModalities,
                model.SystemPromptUpdate ?? existing?.SystemPromptUpdate);
        }
        return [.. merged.Values];
    }
}
