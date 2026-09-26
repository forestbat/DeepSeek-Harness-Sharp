using Dsh.Runtime;

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

/**
 * 账号登录后提供的推理凭据来源:账号插件注册为服务 "inferenceCredentials"。
 * 只有目标 origin 与账号配置的推理 origin 完全一致时才释放令牌,与上游的 inferenceOrigin 边界一致。
 */
public interface IInferenceCredentials
{
    public const string ServiceName = "inferenceCredentials";

    /** 注册阶段: 该目标是否属于账号会供令牌的 origin(不读取令牌)。 */
    bool Covers(Uri destination);

    /** 请求阶段: origin 匹配且已登录时返回令牌,否则返回 null。 */
    string? TryGetToken(Uri destination);
}

/** 账号凭据来源的查取: 未配 API key 时按目标 origin 取令牌。 */
public static class InferenceCredentials
{
    /** 目标 origin 被账号覆盖时返回令牌; 地址非法或没有账号服务时 Covered=false 并保持原有凭据错误。 */
    public static (string? Token, bool Covered) Resolve(Context ctx, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var destination))
            return (null, false);
        var credentials = ctx.Get<IInferenceCredentials>(IInferenceCredentials.ServiceName, false);
        return credentials is null
            ? (null, false)
            : (credentials.TryGetToken(destination), credentials.Covers(destination));
    }
}
