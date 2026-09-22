namespace Dsh.Llm;

/** 适配器插件声明能服务的 wire 类型,以及该 wire 未配置时的默认值。 */
public sealed record LlmWireDefinition(
    string Wire,
    string? DefaultBaseUrl = null,
    string? DefaultApiKeyEnv = null);

/** settings.yaml 的模型条目在契约侧的表达。 */
public sealed record ProviderModelSpec(
    string Id,
    string? Name = null,
    string? SystemPromptUpdate = null,
    bool Reasoning = false);

/** 宿主解析好的 provider 配置:适配器工厂只依赖这份数据构造适配器。 */
public sealed record ResolvedLlmProvider(
    string ProviderId,
    string Wire,
    string BaseUrl,
    string ApiKeyEnv,
    string? ApiKey,
    IReadOnlyList<ProviderModelSpec> Models);

/** 适配器插件在 Apply 中登记它:宿主按 provider 的 wire 类型选择工厂并构造适配器。 */
public interface ILlmAdapterFactory
{
    IReadOnlyList<LlmWireDefinition> Wires { get; }

    LlmAdapter Create(ResolvedLlmProvider provider);
}
