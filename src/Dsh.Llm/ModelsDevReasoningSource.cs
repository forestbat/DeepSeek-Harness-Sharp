namespace Dsh.Llm;

/**
 * models.dev 快照的模型能力源(推理强度与输入模态)。
 * providerKey 用于有明确协议身份的适配器(deepseek/anthropic 等 wire, 与 settings 里用户自定义的 provider 名无关);
 * 通用 openai-compatible 没有身份, 只按 baseUrl 主机名对照 models.dev 的 provider.api;
 * 两者都映射不到(中转站)返回 null, 由上层判定"不提供推理"。
 */
public sealed class ModelsDevReasoningSource : IModelReasoningSource
{
    private readonly string? _providerKey;
    private readonly string? _baseUrl;
    private string? _key;
    private bool _resolved;

    public ModelsDevReasoningSource(string? providerKey = null, string? baseUrl = null)
    {
        _providerKey = providerKey;
        _baseUrl = baseUrl;
    }

    public LlmModelReasoningInfo? ReasoningFor(string model)
        => ProviderKey() is { } key ? ModelsDevCatalog.Embedded.ReasoningFor(key, model) : null;

    /** 模型的输入模态; provider 或模型不在目录里时返回 null(未知, 而不是"仅文本")。 */
    public IReadOnlyList<string>? InputModalitiesFor(string model)
        => ProviderKey() is { } key ? ModelsDevCatalog.Embedded.InputModalitiesFor(key, model) : null;

    /** 模型的上下文窗口 token 数; provider 或模型不在目录里时返回 null(未知, 由上层决定是否可以压缩)。 */
    public int? ContextWindowFor(string model)
        => ProviderKey() is { } key ? ModelsDevCatalog.Embedded.ContextWindowFor(key, model) : null;

    /**
     * 目录里有没有这个模型(它是否支持推理是另一回事)。
     * 用来区分"目录明确说它不支持推理"与"目录根本不认识这个端点/模型": 后者才值得向端点自己探测。
     */
    public bool Knows(string model)
        => ProviderKey() is { } key && ModelsDevCatalog.Embedded.ModelIdsFor(key, null).Contains(model, StringComparer.Ordinal);

    private string? ProviderKey()
    {
        if (!_resolved)
        {
            _key = _providerKey is { Length: > 0 } key ? key : ModelsDevCatalog.Embedded.ProviderKeyFor(_baseUrl);
            _resolved = true;
        }
        return _key;
    }
}
