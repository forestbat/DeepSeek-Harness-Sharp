using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Llm.OpenAi.Plugin.Package)]

namespace Dsh.Llm.OpenAi;

/** OpenAI 兼容 wire 适配器插件: 登记 openai-compatible 族; Chat Completions 与 Responses 由 apiStyle 区分。 */
public sealed class Plugin : IDshPlugin
{
    internal const string Package = "@deepseek-ai/dsh-llm-openai";

    private const string ReasoningEffortsCacheFileName = "reasoning-efforts.json";

    public string[] Inject => [LlmAdapterFactoryRegistry.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName)
            ?? throw new InvalidOperationException("llmAdapterFactories is required for the OpenAI adapter plugin");
        return factories.Register(Package, new OpenAiAdapterFactory(
            OutboundProxy.FromContext(ctx),
            new ReasoningEffortsCache(CacheFile(DshPaths.FromContext(ctx)))));
    }

    /** 推理强度探测结果的落盘位置; 没有持久化根(单测/嵌入)时返回 null, 缓存退化为进程内。 */
    private static string? CacheFile(DshPaths? paths)
        => paths is null ? null : Path.Combine(paths.CachePath, ReasoningEffortsCacheFileName);
}

internal sealed class OpenAiAdapterFactory(OutboundProxy proxy, ReasoningEffortsCache reasoningEfforts) : ILlmAdapterFactory
{
    public IReadOnlyList<LlmWireDefinition> Wires { get; } =
    [
        new("openai-compatible", DefaultApiKeyEnv: "OPENAI_API_KEY"),
    ];

    public LlmAdapter Create(ResolvedLlmProvider provider)
        => new OpenAiCompatibleAdapter(
            provider.ProviderId,
            Endpoint.NormalizeBaseUrl(provider.BaseUrl),
            provider.ApiKey,
            provider.Models,
            new HttpClient(new FinishReasonNormalizingHandler(proxy.CreateHandler())),
            useResponses: string.Equals(provider.ApiStyle, ProviderApiStyles.Responses, StringComparison.OrdinalIgnoreCase),
            reasoningEfforts: reasoningEfforts);
}
