using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Llm.OpenAi.Plugin.Package)]

namespace Dsh.Llm.OpenAi;

/** OpenAI 兼容 wire 适配器插件:登记 openai-compatible / openai-responses 两个 wire 的工厂。 */
public sealed class Plugin : IDshPlugin
{
    internal const string Package = "@deepseek-ai/dsh-llm-openai";

    public string[] Inject => [LlmAdapterFactoryRegistry.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName)
            ?? throw new InvalidOperationException("llmAdapterFactories is required for the OpenAI adapter plugin");
        return factories.Register(Package, new OpenAiAdapterFactory());
    }
}

internal sealed class OpenAiAdapterFactory : ILlmAdapterFactory
{
    public IReadOnlyList<LlmWireDefinition> Wires { get; } =
    [
        new("openai-compatible", DefaultApiKeyEnv: "OPENAI_API_KEY"),
        new("openai-responses", DefaultApiKeyEnv: "OPENAI_API_KEY"),
    ];

    public LlmAdapter Create(ResolvedLlmProvider provider)
        => new OpenAiCompatibleAdapter(
            provider.ProviderId,
            Endpoint.NormalizeBaseUrl(provider.BaseUrl),
            provider.ApiKey,
            provider.Models,
            useResponses: string.Equals(provider.Wire, "openai-responses", StringComparison.OrdinalIgnoreCase));
}
