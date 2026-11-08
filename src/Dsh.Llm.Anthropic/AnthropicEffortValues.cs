namespace Dsh.Llm.Anthropic;

/** 从网关的报错文本里读出它声明的 `output_config.effort` 取值。 */
public static class AnthropicEffortValues
{
    /** 引出取值清单的固定说法(Anthropic 与各网关的方言)。 */
    private static readonly string[] Markers =
    [
        "must be one of",
        "supported values are",
        "supported values:",
        "supported types are",
        "allowed values are",
    ];

    /** 取值清单前后的噪声字符: 引号、反引号、句点、括号、冒号。 */
    private static readonly char[] Trimmable = ['"', '\'', '`', '.', ':', '(', ')', '[', ']', '*', ' '];

    /** 句子结束符: 取值清单到句子结束为止, 后面是 request id 之类的尾巴。 */
    private static readonly char[] SentenceEnd = ['.', '(', ';', '\n', '\r'];

    /**
     * 读 "output_config.effort must be one of minimal, low, medium, high, xhigh, max, or ultra." 这类句子。
     * 只收像档位 id 的短词(字母开头、无空格), 认不出清单时返回空。
     */
    public static IReadOnlyList<string> ParseSupported(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        var lower = text.ToLowerInvariant();
        foreach (var marker in Markers)
        {
            var index = lower.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0 && ValuesOf(text[(index + marker.Length)..]) is { Count: > 0 } values)
                return values;
        }
        return [];
    }

    private static List<string> ValuesOf(string remainder)
    {
        List<string> values = [];
        foreach (var raw in remainder.Split([",", " or ", " and "], StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = raw.Trim().Trim(Trimmable);
            var end = candidate.IndexOfAny(SentenceEnd);
            if (end >= 0)
                candidate = candidate[..end].Trim(Trimmable);
            if (!IsEffortId(candidate))
                break;
            if (!values.Contains(candidate, StringComparer.Ordinal))
                values.Add(candidate);
        }
        return values;
    }

    private static bool IsEffortId(string candidate)
        => candidate.Length is > 0 and <= 12
            && char.IsAsciiLetter(candidate[0])
            && candidate.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
