using Dsh.Runtime;

namespace Dsh.Llm;

/** OpenAI 兼容族内的 API 风格: Chat Completions 与 Responses 同属 openai-compatible, 只是风格不同。 */
public static class ProviderApiStyles
{
    public const string ChatCompletions = "chat-completions";
    public const string Responses = "responses";
}

/** 适配器插件声明能服务的 wire 类型,以及该 wire 未配置时的默认值。 */
public sealed record LlmWireDefinition(
    string Wire,
    string? DefaultBaseUrl = null,
    string? DefaultApiKeyEnv = null,
    /** 该 wire 的模型清单完全由端点 `GET /models` 决定: 不再拿 models.dev 目录往 settings 里补条目。 */
    bool SelfDescribing = false);

/** settings.yaml 的模型条目在契约侧的表达。 */
public sealed record ProviderModelSpec(
    string Id,
    string? Name = null,
    string? SystemPromptUpdate = null);

/** 宿主解析好的 provider 配置:适配器工厂只依赖这份数据构造适配器。 */
public sealed record ResolvedLlmProvider(
    string ProviderId,
    string Wire,
    string BaseUrl,
    string ApiKeyEnv,
    string? ApiKey,
    IReadOnlyList<ProviderModelSpec> Models,
    string? ApiStyle = null);

/** 适配器插件在 Apply 中登记它:宿主按 provider 的 wire 类型选择工厂并构造适配器。 */
public interface ILlmAdapterFactory
{
    IReadOnlyList<LlmWireDefinition> Wires { get; }

    LlmAdapter Create(ResolvedLlmProvider provider);
}

/** 一次推理请求实际携带的凭据: 令牌与它要走的请求头。 */
public enum LlmCredentialScheme
{
    /** `Authorization: Bearer <token>`: 开放平台 API Key 与绝大多数第三方端点。 */
    Bearer,

    /** `x-dsh-auth-token: <token>`: DeepSeek 账号登录换来的平台授权, 与 API Key 互不通用。 */
    DshAuthToken,
}

public sealed record LlmCredential(string Token, LlmCredentialScheme Scheme)
{
    /** 账号授权所在的请求头名; 平台侧同时存在该头时 Authorization 会被忽略。 */
    public const string DshAuthTokenHeader = "x-dsh-auth-token";

    /** 按方案写入请求头: 同一个请求只允许一种凭据, 两种都发会让服务端按账号鉴权。 */
    public void Apply(HttpRequestMessage request)
    {
        var (name, value) = Header();
        request.Headers.TryAddWithoutValidation(name, value);
    }

    /** 通用 HTTP 表达: 账号令牌走 x-dsh-auth-token, API Key 走 Authorization: Bearer。 */
    public (string Name, string Value) Header()
        => Scheme == LlmCredentialScheme.DshAuthToken
            ? (DshAuthTokenHeader, Token)
            : ("Authorization", $"Bearer {Token}");
}

/**
 * 账号登录后提供的推理凭据来源:账号插件注册为服务 "inferenceCredentials"。
 * 只有目标 origin 与账号配置的推理 origin 完全一致时才释放令牌,与上游的 inferenceOrigin 边界一致。
 */
public interface IInferenceCredentials
{
    public const string ServiceName = "inferenceCredentials";

    /** 该来源会供令牌的推理 origin; 未启用账号功能时为 null。 */
    Uri? InferenceOrigin { get; }

    /** 注册阶段: 该目标是否属于账号会供令牌的 origin(不读取令牌)。 */
    bool Covers(Uri destination);

    /** 请求阶段: origin 匹配且已登录时返回令牌,否则返回 null。 */
    string? TryGetToken(Uri destination);

    /** 令牌被端点拒绝时清除登录态; 与当前令牌不同则不动(迟到的拒绝不能清掉后来的登录)。 */
    void RejectToken(string token);
}

/** 账号凭据来源的查取: 未配 API key 时按目标 origin 取令牌。 */
public static class InferenceCredentials
{
    /** 账号会供令牌的推理 origin; 没有账号服务或账号功能关闭时为 null。 */
    public static Uri? Origin(Context ctx)
        => ctx.Get<IInferenceCredentials>(IInferenceCredentials.ServiceName, false)?.InferenceOrigin;

    /** 目标 origin 被账号覆盖时返回账号凭据; 地址非法或没有账号服务时 Covered=false 并保持原有凭据错误。 */
    public static (LlmCredential? Credential, bool Covered) Resolve(Context ctx, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var destination))
            return (null, false);
        var credentials = ctx.Get<IInferenceCredentials>(IInferenceCredentials.ServiceName, false);
        if (credentials is null)
            return (null, false);
        var token = credentials.TryGetToken(destination);
        return (token is { Length: > 0 } ? new LlmCredential(token, LlmCredentialScheme.DshAuthToken) : null,
            credentials.Covers(destination));
    }

    /** 端点拒绝了账号令牌: 让账号服务清掉它(仍是同一令牌才会清), 并把失败改写成"登录态失效"。 */
    public static void Reject(Context ctx, LlmCredential credential)
    {
        if (credential.Scheme != LlmCredentialScheme.DshAuthToken)
            return;
        try
        {
            ctx.Get<IInferenceCredentials>(IInferenceCredentials.ServiceName, false)?.RejectToken(credential.Token);
        }
        catch (Exception)
        {
            // 清凭据失败不能盖掉推理失败本身。
        }
    }
}
