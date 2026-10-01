namespace Dsh.Llm;

/**
 * provider `type` 词汇: 协议族(按协议命名, 不按厂商命名), 以及 OpenAI 族内的 API 风格(Responses 由类型名承载, 不再单列 apiStyle 配置项)。
 * 落盘只写 canonical 名; custom(...) 三项是"自定义端点"的等价别名, 别名与旧值(含曾用厂商名当协议族的 anthropic)读写都归一化成 canonical。
 * 厂商/中转站身份由 provider 名(settings 键)承载, 与 type 无关, 因此 minimax 这类站点也能用 anthropic-messages 线协议。
 */
public static class ProviderTypes
{
    public const string OpenAiCompatible = "openai-compatible";

    public const string OpenAiCompatibleResponses = "openai-compatible(response)";

    public const string AnthropicMessages = "anthropic-messages";

    public const string DeepSeek = "deepseek";

    public const string CustomOpenAiCompatible = "custom(openai-compatible)";

    public const string CustomOpenAiCompatibleResponse = "custom(openai-compatible-response)";

    public const string CustomAnthropicMessages = "custom(anthropic-messages)";

    /** 旧值: 曾被当作与 openai-compatible 并列的协议族。 */
    public const string LegacyOpenAiResponses = "openai-responses";

    /** 旧值: 曾用厂商名当协议族名。 */
    public const string LegacyAnthropic = "anthropic";

    /** 旧值: 曾用 custom(anthropic)。 */
    public const string LegacyCustomAnthropic = "custom(anthropic)";

    /** 类型候选顺序: 4 个 canonical 在前, 3 个自定义别名在后。 */
    public static readonly IReadOnlyList<string> All =
    [
        OpenAiCompatible,
        OpenAiCompatibleResponses,
        AnthropicMessages,
        DeepSeek,
        CustomOpenAiCompatible,
        CustomOpenAiCompatibleResponse,
        CustomAnthropicMessages,
    ];

    /** 归一化结果: 协议族、OpenAI 族内的 API 风格, 以及输入是否不是 canonical 名(别名或旧值)。 */
    public readonly record struct Resolution(string Wire, string? ApiStyle, bool Normalized);

    /** 解析 `type`: 别名与旧值都归到 canonical 语义; 未收录的值按原样当成协议族交给适配器工厂发落。 */
    public static Resolution Parse(string? type)
    {
        var trimmed = type?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Equals(OpenAiCompatible, StringComparison.OrdinalIgnoreCase))
            return new(OpenAiCompatible, ProviderApiStyles.ChatCompletions, false);
        if (trimmed.Equals(OpenAiCompatibleResponses, StringComparison.OrdinalIgnoreCase))
            return new(OpenAiCompatible, ProviderApiStyles.Responses, false);
        if (trimmed.Equals(AnthropicMessages, StringComparison.OrdinalIgnoreCase))
            return new(AnthropicMessages, null, false);
        if (trimmed.Equals(LegacyAnthropic, StringComparison.OrdinalIgnoreCase))
            return new(AnthropicMessages, null, true);
        if (trimmed.Equals(DeepSeek, StringComparison.OrdinalIgnoreCase))
            return new(DeepSeek, null, false);
        if (trimmed.Equals(CustomOpenAiCompatible, StringComparison.OrdinalIgnoreCase))
            return new(OpenAiCompatible, ProviderApiStyles.ChatCompletions, true);
        if (trimmed.Equals(CustomOpenAiCompatibleResponse, StringComparison.OrdinalIgnoreCase))
            return new(OpenAiCompatible, ProviderApiStyles.Responses, true);
        if (trimmed.Equals(CustomAnthropicMessages, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(LegacyCustomAnthropic, StringComparison.OrdinalIgnoreCase))
            return new(AnthropicMessages, null, true);
        if (trimmed.Equals(LegacyOpenAiResponses, StringComparison.OrdinalIgnoreCase))
            return new(OpenAiCompatible, ProviderApiStyles.Responses, true);
        return new(trimmed, null, false);
    }

    /** 落盘用的 canonical 名: 别名、旧值与空值都归一化, 只有未收录的协议族按原样保留。 */
    public static string Canonical(string? type)
    {
        var resolution = Parse(type);
        return resolution.ApiStyle == ProviderApiStyles.Responses
            ? OpenAiCompatibleResponses
            : resolution.Wire;
    }
}
