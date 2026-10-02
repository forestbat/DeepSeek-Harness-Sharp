namespace Dsh.Llm;

/**
 * models.dev 快照的推理能力源。
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
    {
        var catalog = ModelsDevCatalog.Embedded;
        if (!_resolved)
        {
            _key = _providerKey is { Length: > 0 } key ? key : catalog.ProviderKeyFor(_baseUrl);
            _resolved = true;
        }
        return _key is null ? null : catalog.ReasoningFor(_key, model);
    }
}
