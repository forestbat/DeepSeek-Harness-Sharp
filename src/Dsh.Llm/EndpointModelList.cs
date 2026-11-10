using System.Text.Json;

namespace Dsh.Llm;

/** 端点 /models 响应的模型清单: openai-compatible 与 anthropic 的清单接口都是 {"data":[{"id":...}]}。 */
public static class EndpointModelList
{
    /** 响应里的全部模型 id: 保持端点给的顺序并按 id 去重; 结构不符时返回空。 */
    public static IReadOnlyList<string> ParseIds(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
            return [];
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("id", out var idElement)
                || idElement.ValueKind != JsonValueKind.String)
                continue;
            var id = idElement.GetString();
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                ids.Add(id);
        }
        return ids;
    }
}
