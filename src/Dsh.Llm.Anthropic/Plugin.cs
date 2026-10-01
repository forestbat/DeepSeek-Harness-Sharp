using Dsh.Plugins;
using Dsh.Runtime;

[assembly: DshPlugin(Dsh.Llm.Anthropic.Plugin.Package)]

namespace Dsh.Llm.Anthropic;

/** Anthropic wire 适配器插件:登记 anthropic-messages 工厂。 */
public sealed class Plugin : IDshPlugin
{
    internal const string Package = "@deepseek-ai/dsh-llm-anthropic";

    public string[] Inject => [LlmAdapterFactoryRegistry.ServiceName];

    public IDisposable Apply(Context ctx, object? config)
    {
        var factories = ctx.Get<LlmAdapterFactoryRegistry>(LlmAdapterFactoryRegistry.ServiceName)
            ?? throw new InvalidOperationException("llmAdapterFactories is required for the Anthropic adapter plugin");
        return factories.Register(Package, new AnthropicAdapterFactory());
    }
}

internal sealed class AnthropicAdapterFactory : ILlmAdapterFactory
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
            provider.Models.Select(model => model.Id).ToList());
}
