using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Llm.Anthropic.Plugin.Package)]

namespace Dsh.Llm.Anthropic;

/** Anthropic wire 适配器插件:登记 anthropic-messages 工厂。 */
public sealed class Plugin : IDshPlugin
{
    internal const string Package = "@deepseek-ai/dsh-llm-anthropic";

    private const string ReasoningEffortsCacheFileName = "reasoning-efforts.json";

    public string[] Inject => [LlmAdapterFactoryRegistry.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName)
            ?? throw new InvalidOperationException("llmAdapterFactories is required for the Anthropic adapter plugin");
        return factories.Register(Package, new AnthropicAdapterFactory(
            OutboundProxy.FromContext(ctx),
            new ReasoningEffortsCache(CacheFile(DshPaths.FromContext(ctx)))));
    }

    /** 推理强度探测结果的落盘位置; 没有持久化根(单测/嵌入)时返回 null, 缓存退化为进程内。 */
    private static string? CacheFile(DshPaths? paths)
        => paths is null ? null : Path.Combine(paths.CachePath, ReasoningEffortsCacheFileName);
}

internal sealed class AnthropicAdapterFactory(OutboundProxy proxy, ReasoningEffortsCache reasoningEfforts) : ILlmAdapterFactory
{
    public IReadOnlyList<LlmWireDefinition> Wires { get; } =
    [
        new(ProviderTypes.AnthropicMessages, DefaultApiKeyEnv: "ANTHROPIC_API_KEY"),
    ];

    public LlmAdapter Create(ResolvedLlmProvider provider)
        => new AnthropicAdapter(
            provider.ProviderId,
            Endpoint.NormalizeBaseUrl(provider.BaseUrl),
            provider.ApiKey,
            provider.Models.Select(model => model.Id).ToList(),
            new HttpClient(proxy.CreateHandler()),
            reasoningEfforts: reasoningEfforts);
}
